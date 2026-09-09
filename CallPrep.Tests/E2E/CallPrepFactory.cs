// Hosts the real Program.cs in-process (real gate middleware, real DB, real tools, real Whisper) and swaps only the
// authenticate step: an "X-Test-Login" request header becomes the signed-in Microsoft identity. Without the header the
// request is anonymous, exactly like a browser with no cookie. Nothing in Program.cs changes for this; the production
// binary has no test-mode switch.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallPrep.Tests.E2E;

public sealed class CallPrepFactory : WebApplicationFactory<Program>
{
    public const string Scheme = "Test";
    public const string Header = "X-Test-Login";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(TestEnv.RepoRoot, "CallPrep.Api"));   // so wwwroot (built UI) is served like in production
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = Scheme; o.DefaultScheme = Scheme; })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(Scheme, _ => { });
        });
    }

    public HttpClient ClientAs(string? login)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (login is not null) c.DefaultRequestHeaders.Add(Header, login);
        return c;
    }

    sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var login = Request.Headers[Header].FirstOrDefault();
            if (string.IsNullOrEmpty(login)) return Task.FromResult(AuthenticateResult.NoResult());
            // same claim the id_token carries; Program.cs reads preferred_username first
            var id = new ClaimsIdentity([new Claim("preferred_username", login), new Claim(ClaimTypes.Name, login)], CallPrepFactory.Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), CallPrepFactory.Scheme)));
        }
    }
}
