using System.Security.Claims;

namespace FactoryManagementSystem.Services
{
    /// Gives every request an Admin identity, when Auth__Required is false.
    ///
    /// This replaces an earlier attempt that swapped the authorization
    /// POLICY PROVIDER, which did not work and could not have:
    /// [Authorize(Roles = "Admin")] does not ask the provider for a policy
    /// at all. ASP.NET only consults it for a NAMED policy; an attribute
    /// carrying Roles has its role requirement added directly. So reads
    /// came through - the global filter was simply not added - while
    /// saving a layout answered 401 and the app said the session had
    /// expired, of a session that had never existed.
    ///
    /// Handing out an identity is also the more honest shape for what was
    /// asked. The application has no login; everybody using it is
    /// therefore an administrator, and saying so once here is clearer than
    /// arranging for the authorization system to agree by other means.
    /// Anything reading User.Identity.Name still gets a name rather than
    /// an empty one.
    ///
    /// It never overwrites a real sign-in. A deployment running with
    /// Auth__Required=true does not reach this at all, and one that is
    /// handed a valid token keeps the user that token named.
    public sealed class OpenAccessMiddleware
    {
        private readonly RequestDelegate _next;

        public OpenAccessMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                // An authentication type has to be set, or the identity
                // reports itself unauthenticated whatever claims it holds
                // and every [Authorize] still refuses it.
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, "open-access"),
                        new Claim(ClaimTypes.Role, "Admin"),
                    },
                    authenticationType: "OpenAccess");

                context.User = new ClaimsPrincipal(identity);
            }

            await _next(context);
        }
    }
}
