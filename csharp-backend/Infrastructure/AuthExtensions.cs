namespace PacienteRcv.Infrastructure;

public static class AuthExtensions
{
    public static bool IsAuthenticated(this HttpContext context)
    {
        return string.Equals(context.Session.GetString("autenticado"), "true", StringComparison.Ordinal);
    }
}
