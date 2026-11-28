namespace FactoryManagementSystem.Services.Sessions
{
    /// Turns away a request whose session has ended, and notes that the
    /// ones that pass are still being made.
    ///
    /// This is the half of signing out that a token cannot do by
    /// itself. A JWT is good until it expires and nothing can take it
    /// back; what can be taken back is the session it names, and this
    /// is where that is read.
    ///
    /// It sits after authentication, so the claims are already there,
    /// and it costs no query of its own - see SessionTracker.
    public sealed class SessionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly SessionTracker _sessions;

        public SessionMiddleware(RequestDelegate next, SessionTracker sessions)
        {
            _next = next;
            _sessions = sessions;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var id = JwtTokenService.SessionOf(context.User);

            if (id != null && !await _sessions.IsLiveAsync(id))
            {
                // 401 and not 403: the credential is the thing that is
                // no longer good, and the app's own handling of 401
                // already takes somebody back to the login page.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"success\":false,\"reason\":\"sessionEnded\","
                    + "\"message\":\"This device has been signed out.\"}");
                return;
            }

            _sessions.Touch(id);
            await _next(context);
        }
    }
}
