using System;
using System.Threading.Tasks;
using System.Windows;

namespace Youtube_TV.Desktop
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e != null && e.Args != null)
            {
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (string.Equals(e.Args[i], "--sync-xbox-tile", StringComparison.OrdinalIgnoreCase))
                    {
                        XboxTileArtworkService.SyncIfRegistered();
                        Shutdown(0);
                        return;
                    }
                }
            }

            Task.Run(delegate { XboxTileArtworkService.SyncIfRegistered(); });
            base.OnStartup(e);
        }
    }
}
