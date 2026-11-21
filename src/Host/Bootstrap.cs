using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CodexSwitch
{
    internal static class Bootstrap
    {
        private static Assembly core;

        [STAThread]
        internal static int Main(string[] args)
        {
            using (Stream stream = typeof(Bootstrap).Assembly.GetManifestResourceStream("CodexSwitch.Core.dll"))
            using (var image = new MemoryStream())
            {
                stream.CopyTo(image);
                core = Assembly.Load(image.ToArray());
            }
            AppDomain.CurrentDomain.AssemblyResolve += ResolveCore;
            return Run(args);
        }

        private static Assembly ResolveCore(object sender, ResolveEventArgs args)
        {
            return String.Equals(args.Name, core.FullName, StringComparison.OrdinalIgnoreCase) ? core : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
#if CLI
            return CliProgram.Run(args);
#else
            return Program.Main(args);
#endif
        }
    }
}
