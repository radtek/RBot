﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Diagnostics;
using System.CodeDom.Compiler;
using Microsoft.CodeDom.Providers.DotNetCompilerPlatform;

using RBot.Options;

namespace RBot
{
    public class ScriptManager
    {
        public static List<string> DefaultRefs { get; } = new List<string>()
        {
            "System.dll",
            "System.Core.dll",
            "System.Linq.dll",
            "System.Data.dll",
            "System.Drawing.dll",
            "System.Net.dll",
            "System.Windows.Forms.dll",
            "System.Xml.dll",
            "System.Xml.Linq.dll"
        };

        public static Thread CurrentScriptThread { get; set; }
        public static bool ScriptRunning => CurrentScriptThread?.IsAlive ?? false;
        public static string LoadedScript { get; set; }

        public static event Action ScriptStarted;
        public static event Action<bool> ScriptStopped;
        public static event Action<Exception> ScriptError;

        private static CodeDomProvider _provider = new CSharpCodeProvider();
        private static Dictionary<string, bool> _configured = new Dictionary<string, bool>();
        private static List<string> _refCache = new List<string>();

        public static async Task<Exception> StartScriptAsync()
        {
            if (ScriptRunning)
            {
                ScriptInterface.Instance.Log("Script already running.");
                return new Exception("Script already running.");
            }
            else
            {
                try
                {
                    ScriptInterface.exit = false;
                    object script = await Task.Run(() => Compile(File.ReadAllText(LoadedScript)));
                    LoadScriptConfig(script);
                    if (_configured.TryGetValue(ScriptInterface.Instance.Config.Storage, out bool b) && !b)
                        ScriptInterface.Instance.Config.Configure();
                    ScriptInterface.Instance.Handlers.Clear();
                    ScriptInterface.Instance.Runtime = new ScriptRuntimeVars();
                    CurrentScriptThread = new Thread(() =>
                    {
                        ScriptStarted?.Invoke();
                        try
                        {
                            script.GetType().GetMethod("ScriptMain").Invoke(script, new object[] { ScriptInterface.Instance });
                        }
                        catch (Exception e)
                        {
                            if (!(e is ThreadAbortException))
                            {
                                Debug.WriteLine($"Error while running script:\r\n{e}");
                                ScriptError?.Invoke(e);
                            }
                        }
                        ScriptStopped?.Invoke(true);
                    });
                    CurrentScriptThread.Name = "Script Thread";
                    CurrentScriptThread.Start();
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            }
        }

        public static Exception RestartScript()
        {
            if (!ScriptRunning)
                return new Exception("Script not running.");
            StopScript();
            Task<Exception> task = Task.Run(StartScriptAsync);
            task.Wait();
            return task.Result;
        }

        public static void LoadScriptConfig(object script)
        {
            ScriptOptionContainer opts = ScriptInterface.Instance.Config = new ScriptOptionContainer();
            Type t = script.GetType();
            FieldInfo storageField = t.GetField("OptionsStorage");
            FieldInfo optsField = t.GetField("Options");
            FieldInfo dontPreconfField = t.GetField("DontPreconfigure");
            if (optsField != null)
                opts.Options.AddRange((List<IOption>)optsField.GetValue(script));
            if (storageField != null)
                opts.Storage = (string)storageField.GetValue(script);
            if (dontPreconfField != null)
                _configured[opts.Storage] = (bool)dontPreconfField.GetValue(script);
            else if (optsField != null)
                _configured[opts.Storage] = false;
            opts.SetDefaults();
            opts.Load();
        }

        public static void StopScript()
        {
            ScriptInterface.exit = true;
            CurrentScriptThread?.Join(1000);
            if (CurrentScriptThread?.IsAlive ?? false)
            {
                CurrentScriptThread.Abort();
                ScriptStopped?.Invoke(false);
            }
            CurrentScriptThread = null;
        }

        public static object Compile(string source)
        {
            CompilerParameters opts = new CompilerParameters();
            opts.GenerateInMemory = true;
            opts.GenerateExecutable = false;
            opts.TreatWarningsAsErrors = false;
            opts.ReferencedAssemblies.Add(typeof(ScriptManager).Assembly.Location);
            opts.ReferencedAssemblies.AddRange(DefaultRefs.Select(r => File.Exists(r) ? Path.Combine(Environment.CurrentDirectory, r) : r).ToArray());
            if (_refCache.Count == 0)
            {
                _refCache.AddRange(Directory.GetFiles(".", "*.dll").Select(x => Path.Combine(Environment.CurrentDirectory, x)).Where(CanLoadAssembly));
                if (Directory.Exists("plugins"))
                    _refCache.AddRange(Directory.GetFiles("plugins", "*.dll").Select(x => Path.Combine(Environment.CurrentDirectory, x)).Where(CanLoadAssembly));
            }
            opts.ReferencedAssemblies.AddRange(_refCache.ToArray());
            foreach (string line in source.Split('\n').Select(l => l.Trim()))
            {
                if (line.StartsWith("using"))
                    break;
                if (line.StartsWith("//cs_"))
                {
                    string[] parts = line.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                    string cmd = parts[0].Substring(5);
                    switch (cmd)
                    {
                        case "ref":
                            string local = Path.Combine(Environment.CurrentDirectory, parts[1]);
                            if (File.Exists(local))
                                opts.ReferencedAssemblies.Add(local);
                            else
                                opts.ReferencedAssemblies.Add(parts[1]);
                            break;
                    }
                }
            }
            Stopwatch sw = new Stopwatch();
            sw.Start();
            CompilerResults results = _provider.CompileAssemblyFromSource(opts, source);
            sw.Stop();
            Debug.WriteLine("Compile: " + sw.Elapsed.TotalMilliseconds);
            if (results.Errors.Count == 0)
            {
                Type t = results.CompiledAssembly.DefinedTypes.First(t => t.GetDeclaredMethod("ScriptMain") != null);
                if (t == null)
                    throw new Exception("No declared type with entry point found.");
                return Activator.CreateInstance(t);
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                foreach (CompilerError error in results.Errors)
                    sb.AppendLine(error.ToString());
                throw new ScriptCompileException(sb.ToString());
            }
        }

        private static bool CanLoadAssembly(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
