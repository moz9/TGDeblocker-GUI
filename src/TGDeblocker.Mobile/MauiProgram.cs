using TGDeblocker.Mobile.Rendering;

namespace TGDeblocker.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<GlassSurface, GlassSurfaceHandler>());
        return builder.Build();
    }
}
