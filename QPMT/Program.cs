using System;
using System.Windows.Forms;

namespace QPMT
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QPDTForm());
        }
    }
}
