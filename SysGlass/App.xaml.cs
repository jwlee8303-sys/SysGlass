using System.Windows;
using System.Windows.Threading;
using SysGlass.Utilities;

namespace SysGlass
{
    public partial class App : Application
    {
        public App()
        {
            // 처리되지 않은 예외는 로그만 남기고 프로그램이 종료되지 않게 한다.
            DispatcherUnhandledException += OnUiException;
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Logger.Error("백그라운드 작업 예외", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>진단용: `SysGlass.exe --dump-sensors 경로` 로 센서 전체 목록을 파일로 저장하고 종료한다.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length >= 2 && e.Args[0] == "--dump-sensors")
            {
                SensorDump.Write(e.Args[1]);
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("UI 예외", e.Exception);
            e.Handled = true;
        }
    }
}
