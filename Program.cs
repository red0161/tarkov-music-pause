using System;
using System.Windows.Forms;
using TarkovMusicPause;

namespace TarkovMusicPause
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
