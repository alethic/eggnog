using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Runs one test of this assembly in a process of its own, which runs nothing else, and returns the figures it
    /// reports: for measurements of the whole process's heap, which in a full run holds whatever the earlier tests left
    /// and moves with it. The child is the test assembly started again as this process was started: by its app host
    /// (<c>Eggnog.Tests.exe</c>, as <c>dotnet test</c> starts it, or as it is run by hand), or by the <c>dotnet</c>
    /// host with <c>exec</c> and the assembly's path (<c>dotnet exec Eggnog.Tests.dll</c>, as CI runs it), with a
    /// filter for the one test method and <see cref="Variable"/> set to the file its figures go to. The child inherits
    /// this process's environment, so it runs under the same runtime and settings (<c>EGGNOG_COUNTERS</c> among them).
    /// A test that reports figures skips unless <see cref="Variable"/> is set (<see cref="FiguresPath"/>). A child that
    /// cannot start, runs past its timeout, exits with a failure or writes no figures fails the calling test.
    /// </summary>
    public static class IsolatedRun
    {

        /// <summary>
        /// The environment variable that names the file a child writes its figures to.
        /// </summary>
        public const string Variable = "EGGNOG_ISOLATED_FIGURES";

        /// <summary>
        /// The most of a child's output a failure quotes, from its end.
        /// </summary>
        const int QuotedOutput = 8_000;

        /// <summary>
        /// Gets the file this process writes its figures to when it is a child, or <see langword="null"/> when it is
        /// not.
        /// </summary>
        public static string? FiguresPath
        {
            get
            {
                var path = Environment.GetEnvironmentVariable(Variable);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        /// <summary>
        /// Runs the test method <paramref name="method"/> of <paramref name="type"/> in a child process and returns
        /// the figures it wrote (<see cref="Write"/>), by name. Writes the child's command line to
        /// <paramref name="output"/>. Fails the calling test if the child cannot start, runs longer than
        /// <paramref name="timeout"/> (the child is killed), exits with a failure, or writes no figures; the failure
        /// quotes the end of the child's output.
        /// </summary>
        /// <param name="type">The test class, in this assembly.</param>
        /// <param name="method">The test method's name.</param>
        /// <param name="timeout">How long the child may run.</param>
        /// <param name="output">The calling test's output.</param>
        /// <returns>The figures, by name.</returns>
        public static IReadOnlyDictionary<string, long[]> Run(Type type, string method, TimeSpan timeout, ITestOutputHelper output)
        {
            var assembly = type.Assembly.Location;
            var host = Environment.ProcessPath;
            if (string.IsNullOrEmpty(assembly) || string.IsNullOrEmpty(host))
                Assert.Fail($"Cannot start a child: the process path is '{host}' and the test assembly's is '{assembly}'.");

            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
            };
            if (IsAppHost(host, assembly) == false)
            {
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add(assembly);
            }

            foreach (var argument in new[] { "-method", type.FullName + "." + method, "-noLogo", "-noColor", "-noAutoReporters" })
                start.ArgumentList.Add(argument);

            var path = Path.Combine(Path.GetTempPath(), "eggnog-figures-" + Guid.NewGuid().ToString("N") + ".txt");
            start.Environment[Variable] = path;
            var command = host + " " + string.Join(" ", start.ArgumentList);
            output.WriteLine($"child: {command}");
            try
            {
                return RunChild(start, command, path, timeout);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Writes the figures of the test running in this child to <see cref="FiguresPath"/>, one line per figure: its
        /// name, then its values.
        /// </summary>
        /// <param name="figures">The figures, by name; a name has no spaces.</param>
        public static void Write(IReadOnlyDictionary<string, long[]> figures)
        {
            var path = FiguresPath ?? throw new InvalidOperationException($"{Variable} is not set: this process is not a child.");
            var lines = figures.Select(f => f.Key + " " + string.Join(" ", f.Value.Select(v => v.ToString(CultureInfo.InvariantCulture))));
            File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// Gets whether <paramref name="host"/> is the test assembly's own app host, which runs it with no
        /// <c>exec</c>: its file name is the assembly's, with no extension or with <c>.exe</c>.
        /// </summary>
        /// <param name="host">The path of this process's executable.</param>
        /// <param name="assembly">The path of the test assembly.</param>
        /// <returns>Whether the host is the app host.</returns>
        static bool IsAppHost(string host, string assembly)
        {
            var name = Path.GetFileName(host);
            var assemblyName = Path.GetFileNameWithoutExtension(assembly);
            return string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, assemblyName + ".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Starts the child, waits for it, and reads its figures.
        /// </summary>
        /// <param name="start">The child's start information.</param>
        /// <param name="command">The child's command line, for failures.</param>
        /// <param name="path">The file the child writes its figures to.</param>
        /// <param name="timeout">How long the child may run.</param>
        /// <returns>The figures, by name.</returns>
        static IReadOnlyDictionary<string, long[]> RunChild(ProcessStartInfo start, string command, string path, TimeSpan timeout)
        {
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception e)
            {
                Assert.Fail($"The child did not start ({e.Message}): {command}");
                throw;
            }

            if (process is null)
                Assert.Fail($"The child did not start: {command}");

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(timeout);
                if (exited == false)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                else
                {
                    process.WaitForExit();
                }

                Task.WaitAll([stdout, stderr], TimeSpan.FromMinutes(1));
                var text = (stdout.IsCompletedSuccessfully ? stdout.Result : "") + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
                if (text.Length > QuotedOutput)
                    text = "..." + text[^QuotedOutput..];

                if (exited == false)
                    Assert.Fail($"The child ran longer than {timeout} and was killed: {command}{Environment.NewLine}{text}");
                if (process.ExitCode != 0)
                    Assert.Fail($"The child exited with {process.ExitCode}: {command}{Environment.NewLine}{text}");
                if (File.Exists(path) == false)
                    Assert.Fail($"The child reported nothing: {command}{Environment.NewLine}{text}");

                var figures = new Dictionary<string, long[]>();
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    figures[parts[0]] = parts.Skip(1).Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                }

                if (figures.Count == 0)
                    Assert.Fail($"The child reported no figures: {command}{Environment.NewLine}{text}");

                return figures;
            }
        }

    }

}
