using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<StoreDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.Cookie.Name = "SolarPowers.Admin"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(); builder.Services.AddHttpClient();
var app = builder.Build();
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<StoreDb>(); db.Database.EnsureCreated();
    if (!db.Items.Any(x => x.Collection == "products")) {
        var seedPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "seed.json");
        if (File.Exists(seedPath)) {
            using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));
            foreach (var col in doc.RootElement.EnumerateObject()) foreach (var item in col.Value.EnumerateObject())
                db.Items.Add(new StoreItem { Id = item.Name, Collection = col.Name, Json = item.Value.GetRawText() });
            db.SaveChanges();
        }
    }
}
app.UseExceptionHandler(error => error.Run(async context => { context.Response.StatusCode=500; context.Response.ContentType="application/json"; await context.Response.WriteAsJsonAsync(new { error="An unexpected server error occurred." }); }));
app.UseDefaultFiles(); app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization();
Func<ClaimsPrincipal, bool> admin = u => u.Identity?.IsAuthenticated == true && u.IsInRole("Admin");
app.MapGet("/api/health", () => Results.Ok(new { ok=true, database="SQLite" }));
app.MapGet("/api/auth/me", (ClaimsPrincipal u) => Results.Ok(new { authenticated=admin(u) }));
app.MapPost("/api/auth/login", async Task<IResult> (LoginRequest req, HttpContext ctx, IConfiguration cfg) => {
    var expectedUser=cfg["Admin:Username"] ?? "admin"; var expectedPass=cfg["Admin:Password"] ?? "ChangeThisNow!";
    var a=Encoding.UTF8.GetBytes(req.Username ?? ""); var b=Encoding.UTF8.GetBytes(expectedUser);
    var p=Encoding.UTF8.GetBytes(req.Password ?? ""); var q=Encoding.UTF8.GetBytes(expectedPass);
    if (a.Length!=b.Length || p.Length!=q.Length || !CryptographicOperations.FixedTimeEquals(a,b) || !CryptographicOperations.FixedTimeEquals(p,q)) return Results.Json(new { error="Invalid admin ID or password." },statusCode:401);
    var claims=new[]{new Claim(ClaimTypes.Name,expectedUser),new Claim(ClaimTypes.Role,"Admin")};
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { authenticated=true });
});
app.MapPost("/api/auth/logout", async Task<IResult> (HttpContext ctx) => { await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok(new { authenticated=false }); });
app.MapGet("/api/data/{collection}", async Task<IResult> (string collection, ClaimsPrincipal user, StoreDb db) => {
    if (!IsKnown(collection)) return Results.NotFound();
    if (collection is not ("products" or "gallery") && !admin(user)) return Results.Unauthorized();
    var items=await db.Items.Where(x=>x.Collection==collection).OrderBy(x=>x.Id).ToListAsync();
    var result=new List<Dictionary<string,object?>>();
    foreach(var item in items){ using var d=JsonDocument.Parse(item.Json); var row=JsonSerializer.Deserialize<Dictionary<string,object?>>(item.Json) ?? new(); row["id"]=item.Id; result.Add(row); }
    return Results.Ok(result);
});
app.MapPut("/api/data/{collection}/{id}", async Task<IResult> (string collection,string id,JsonElement data,ClaimsPrincipal user,StoreDb db) => {
    if (!IsKnown(collection)) return Results.NotFound();
    if ((collection is "products" or "gallery") && !admin(user)) return Results.Unauthorized();
    if (collection is not ("products" or "gallery" or "orders" or "enquiries" or "feedback")) return Results.BadRequest(new {error="Unknown collection."});
    if (data.ValueKind!=JsonValueKind.Object || data.GetRawText().Length>1_500_000) return Results.BadRequest(new {error="Invalid or oversized data."});
    var item=await db.Items.FirstOrDefaultAsync(x=>x.Collection==collection&&x.Id==id);
    if(item is null){ item=new StoreItem{Id=id,Collection=collection,Json=data.GetRawText()};db.Items.Add(item); }
    else { if(collection is not ("products" or "gallery") && !admin(user)) return Results.Unauthorized(); item.Json=data.GetRawText(); }
    await db.SaveChangesAsync(); return Results.Ok(new { id });
});
app.MapPatch("/api/data/{collection}/{id}", async Task<IResult> (string collection,string id,JsonElement patch,ClaimsPrincipal user,StoreDb db) => {
    if (!admin(user)) return Results.Unauthorized(); if(!IsKnown(collection)) return Results.NotFound();
    var item=await db.Items.FirstOrDefaultAsync(x=>x.Collection==collection&&x.Id==id); if(item is null)return Results.NotFound();
    using var oldDoc=JsonDocument.Parse(item.Json); var dict=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(item.Json) ?? new();
    foreach(var p in patch.EnumerateObject()) dict[p.Name]=p.Value.Clone(); item.Json=JsonSerializer.Serialize(dict); await db.SaveChangesAsync(); return Results.Ok(new{id});
});
app.MapDelete("/api/data/{collection}/{id}", async Task<IResult> (string collection,string id,ClaimsPrincipal user,StoreDb db) => {
    if(!admin(user)) return Results.Unauthorized(); if(!IsKnown(collection))return Results.NotFound();
    var item=await db.Items.FirstOrDefaultAsync(x=>x.Collection==collection&&x.Id==id); if(item is null)return Results.NoContent(); db.Items.Remove(item); await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapPost("/api/chat", async Task<IResult> (ChatRequest req,IConfiguration cfg,IHttpClientFactory factory,StoreDb db) => {
    var key=cfg["Anthropic:ApiKey"]; if(string.IsNullOrWhiteSpace(key))return Results.Ok(new{reply="Solar Expert chat is ready to connect. Add your Anthropic API key in appsettings.json to enable AI replies."});
    var messages=(req.Messages??new()).TakeLast(6).Where(m=>(m.Role=="user"||m.Role=="assistant")&&!string.IsNullOrWhiteSpace(m.Content)).Select(m=>new{role=m.Role,content=m.Content.Length>1500?m.Content[..1500]:m.Content}).ToList();
    if(messages.Count==0)return Results.BadRequest(new{error="Message required."});
    var products=await db.Items.Where(x=>x.Collection=="products").Take(60).ToListAsync(); var catalog=string.Join("\n",products.Select(x=>{using var d=JsonDocument.Parse(x.Json);var r=d.RootElement;return $"{Get(r,"name")} ({Get(r,"cat")}) ₹{Get(r,"price")}: {Get(r,"desc")}";}));
    var body=new{model=cfg["Anthropic:Model"]??"claude-3-5-sonnet-20241022",max_tokens=600,system="You are Solar Expert for an Indian solar store. Answer briefly in the customer's language and recommend products from this catalog.\n"+catalog,messages};
    try{var request=new HttpRequestMessage(HttpMethod.Post,"https://api.anthropic.com/v1/messages"){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};request.Headers.Add("x-api-key",key);request.Headers.Add("anthropic-version","2023-06-01");var response=await factory.CreateClient().SendAsync(request);if(!response.IsSuccessStatusCode)return Results.Ok(new{reply="Sorry, I could not answer right now."});using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return Results.Ok(new{reply=doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()});}catch{return Results.Ok(new{reply="Sorry, I could not answer right now."});}
});
app.Run();
static bool IsKnown(string c)=>c is "products" or "gallery" or "orders" or "enquiries" or "feedback";
static string Get(JsonElement e,string name)=>e.TryGetProperty(name,out var v)?v.ToString():"";
public sealed class StoreDb(DbContextOptions<StoreDb> options):DbContext(options){public DbSet<StoreItem> Items=>Set<StoreItem>();protected override void OnModelCreating(ModelBuilder b){b.Entity<StoreItem>().HasKey(x=>new{x.Collection,x.Id});b.Entity<StoreItem>().Property(x=>x.Collection).HasMaxLength(32);b.Entity<StoreItem>().Property(x=>x.Id).HasMaxLength(100);}}
public sealed class StoreItem{public string Collection{get;set;}="";public string Id{get;set;}="";public string Json{get;set;}="{}";}
public sealed record LoginRequest(string? Username,string? Password);
public sealed record ChatMessage(string Role,string Content);
public sealed record ChatRequest(List<ChatMessage>? Messages);
