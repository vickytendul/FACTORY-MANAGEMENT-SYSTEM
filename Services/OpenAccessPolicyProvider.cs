using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FactoryManagementSystem.Services
{
    /// Satisfies every authorization policy, so the API answers without a
    /// token. Registered ONLY when Auth:Required is false.
    ///
    /// The global AuthorizeFilter is not the whole of it: controllers also
    /// carry their own [Authorize(Roles = "Admin")], and those build a
    /// policy out of the role names rather than using the default one. So
    /// the policy PROVIDER is replaced rather than the default policy, and
    /// every policy it is asked for - default, fallback, or one named after
    /// a role - comes back already satisfied.
    ///
    /// What this means plainly: with the flag off, anyone who knows the URL
    /// can read and write this factory's data. The deployment is on the
    /// public internet. That is the trade being made for an app that opens
    /// without a login, and it is reversible by setting Auth__Required=true.
    public sealed class OpenAccessPolicyProvider : IAuthorizationPolicyProvider
    {
        private static readonly AuthorizationPolicy Open =
            new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => true)
                .Build();

        public OpenAccessPolicyProvider(IOptions<AuthorizationOptions> options)
        {
            // The options are taken but unused: the base provider reads them
            // to find named policies, and there are none to find here.
            _ = options;
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
            Task.FromResult(Open);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
            Task.FromResult<AuthorizationPolicy?>(Open);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            Task.FromResult<AuthorizationPolicy?>(Open);
    }
}
