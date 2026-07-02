using System.Windows;
using MajesticParser.Services;

namespace MajesticParser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (BrowserService.IsElevated())
        {
            MessageBox.Show(
                "Приложение запущено от имени администратора.\n\n" +
                "Google Chrome не запускается под администратором, поэтому парсинг работать не будет.\n\n" +
                "Закройте приложение и запустите его как обычный пользователь " +
                "(без «Запуск от имени администратора»).",
                "Запуск от администратора",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
