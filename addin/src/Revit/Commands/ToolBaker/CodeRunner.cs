// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Runtime/SendCodeToRevitHandler.cs, src/shared/Infrastructure/Scripting/SendCodeSourceBuilder.cs,
// src/shared/Infrastructure/Scripting/ScriptReferences.cs and src/shared/ToolBaker/ToolCompiler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: one small compiler shared by send_code_to_revit and run_baked_tool. The user's code is a
// method body (plus optional usings and helper types) wrapped into one fixed entry point
// B2MScript.Run(app, uidoc, doc, args) and run inside a single named Transaction that is rolled back on any
// error. Script protocol, modules, compile-only mode, journaling, telemetry, history, receipts, bake policy and
// redaction are not ported. On .NET 8+ the assembly is loaded into the add-in's own load context so it sees
// the same RevitAPI and Newtonsoft.Json types as the add-in.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Meta;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.ToolBaker
{
    /// <summary>
    /// Compiles C# into an in-memory assembly and runs it on Revit's main thread inside one transaction.
    /// Callers check developer mode and write the audit log; this class only compiles and runs.
    /// </summary>
    internal static class CodeRunner
    {
        public const string EntryType = "B2MScript";
        private const string SourceName = "code";
        private const int MaxDiagnostics = 10;

        private static readonly string[] DefaultImports =
        {
            "System", "System.Linq", "System.Collections.Generic", "Autodesk.Revit.DB",
            "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI", "Newtonsoft.Json.Linq"
        };

        /// <summary>How the code is written, for command descriptions.</summary>
        public const string CodeContract =
            "The code is a C# method body. In scope: app (UIApplication), uidoc (UIDocument), doc (Document), args (JObject). "
            + "Usings for System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ .Architecture, .Structure), "
            + "Autodesk.Revit.UI and Newtonsoft.Json.Linq are added; extra usings and helper classes may follow the statements. "
            + "Return a value (number, string, anonymous object, JObject, Element or ElementId) to get it back. "
            + "The code already runs inside one Revit transaction: do not start your own Transaction (SubTransaction is fine). "
            + "Any exception rolls everything back.";

        /// <summary>
        /// The developer-mode-gated path both send_code_to_revit and run_baked_tool use: write a "started" record with
        /// the full code to the code-execution log (refuse if it cannot be written), compile and run, then write the outcome.
        /// The caller has already checked developer mode.
        /// </summary>
        public static CommandResult AuditedRun(UIApplication app, string command, string tool, string code, JObject args, string transactionName)
        {
            var year = app.Application.VersionNumber;
            var runId = Guid.NewGuid().ToString("N");
            if (!DeveloperMode.Log(year, runId, command, tool, code, "started"))
                return CommandResult.Fail("AUDIT_LOG_UNAVAILABLE: the code-execution log (%LOCALAPPDATA%\\B2M\\logs\\code-execution.jsonl) "
                    + "could not be written, so the code was not run. Nothing ran.");

            var outcome = "error";
            CommandResult result = null;
            try
            {
                result = Run(app, code, args, transactionName, out outcome);
                return result;
            }
            finally
            {
                DeveloperMode.Log(year, runId, command, tool, code, outcome, result != null && !result.Success ? result.Error : null);
            }
        }

        /// <summary>
        /// Compile and run <paramref name="code"/>. <paramref name="outcome"/> is a short word for the audit log:
        /// ok, no_document, compile_failed, runtime_error, rolled_back or error.
        /// </summary>
        public static CommandResult Run(UIApplication app, string code, JObject args, string transactionName, out string outcome)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
            {
                outcome = "no_document";
                return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            }

            byte[] bytes;
            JArray diagnostics;
            try
            {
                bytes = Compile(code, out diagnostics);
            }
            catch (Exception ex)
            {
                outcome = "error";
                return CommandResult.Fail("COMPILER_UNAVAILABLE: the C# compiler could not run: " + ex.Message);
            }
            if (bytes == null)
            {
                outcome = "compile_failed";
                var first = diagnostics.FirstOrDefault()?.Value<string>("text") ?? "unknown error";
                return CommandResult.Fail("COMPILE_FAILED: " + first + " Nothing ran.",
                    new JObject { ["compiled"] = false, ["executed"] = false, ["diagnostics"] = diagnostics });
            }

            MethodInfo entry;
            try
            {
                entry = Load(bytes).GetType(EntryType)?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                outcome = "error";
                return CommandResult.Fail("LOAD_FAILED: the compiled code could not be loaded: " + ex.Message + " Nothing ran.");
            }
            if (entry == null)
            {
                outcome = "error";
                return CommandResult.Fail("LOAD_FAILED: the compiled code has no " + EntryType + ".Run entry point. Nothing ran.");
            }

            object output;
            using (var tx = new Transaction(doc, transactionName))
            {
                try
                {
                    tx.Start();
                    output = entry.Invoke(null, new object[] { app, uidoc, doc, args ?? new JObject() });
                }
                catch (Exception ex)
                {
                    RollBack(tx);
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    outcome = "runtime_error";
                    return CommandResult.Fail("RUNTIME_ERROR: " + inner.GetType().Name + ": " + inner.Message + " All changes were rolled back.",
                        new JObject { ["compiled"] = true, ["executed"] = true, ["rolled_back"] = true });
                }

                TransactionStatus status;
                try
                {
                    status = tx.GetStatus() == TransactionStatus.Started ? tx.Commit() : tx.GetStatus();
                }
                catch (Exception ex)
                {
                    RollBack(tx);
                    outcome = "rolled_back";
                    return CommandResult.Fail("NOT_COMMITTED: Revit refused to commit the changes: " + ex.Message + " All changes were rolled back.");
                }
                if (status != TransactionStatus.Committed)
                {
                    outcome = "rolled_back";
                    return CommandResult.Fail("NOT_COMMITTED: Revit reported " + status + "; the code's changes were not kept. See failures.",
                        new JObject { ["compiled"] = true, ["executed"] = true, ["rolled_back"] = true });
                }
            }

            outcome = "ok";
            return CommandResult.Ok(new JObject { ["executed"] = true, ["result"] = ToJson(output) });
        }

        /// <summary>The generated source, for tests and error reports. Line numbers in diagnostics refer to the user's code.</summary>
        internal static string BuildSource(string code)
        {
            var root = CSharpSyntaxTree.ParseText(code ?? "").GetCompilationUnitRoot();
            var sb = new StringBuilder();
            foreach (var node in root.Externs) sb.Append(Mapped(node));
            foreach (var node in root.Usings) sb.Append(Mapped(node));
            sb.Append("#line hidden\n");
            foreach (var name in DefaultImports)
                if (!root.Usings.Any(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name.ToString() == name))
                    sb.Append("using ").Append(name).Append(";\n");
            foreach (var node in root.AttributeLists) sb.Append(Mapped(node));
            sb.Append("#line hidden\npublic static class ").Append(EntryType).Append(" {\n")
              .Append(" public static object Run(global::Autodesk.Revit.UI.UIApplication app, global::Autodesk.Revit.UI.UIDocument uidoc,")
              .Append(" global::Autodesk.Revit.DB.Document doc, global::Newtonsoft.Json.Linq.JObject args) {\n");
            foreach (var statement in root.Members.OfType<GlobalStatementSyntax>()) sb.Append(Mapped(statement));
            sb.Append("#line hidden\n  return null;\n }\n}\n");
            foreach (var member in root.Members.Where(m => !(m is GlobalStatementSyntax))) sb.Append(Mapped(member));
            return sb.ToString();
        }

        /// <summary>Returns the assembly bytes, or null with the errors in <paramref name="diagnostics"/>.</summary>
        private static byte[] Compile(string code, out JArray diagnostics)
        {
            var tree = CSharpSyntaxTree.ParseText(BuildSource(code));
            var compilation = CSharpCompilation.Create("B2MScript_" + Guid.NewGuid().ToString("N"), new[] { tree }, References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var stream = new MemoryStream())
            {
                var emitted = compilation.Emit(stream);
                diagnostics = new JArray(emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(MaxDiagnostics)
                    .Select(Describe));
                return emitted.Success ? stream.ToArray() : null;
            }
        }

        private static JObject Describe(Diagnostic d)
        {
            var span = d.Location.GetMappedLineSpan();
            var inCode = span.Path == SourceName;
            var line = inCode ? span.StartLinePosition.Line + 1 : (int?)null;
            var column = inCode ? span.StartLinePosition.Character + 1 : (int?)null;
            var where = inCode ? "line " + line + ", column " + column : "generated wrapper";
            return new JObject
            {
                ["id"] = d.Id,
                ["line"] = line,
                ["column"] = column,
                ["message"] = d.GetMessage(),
                ["text"] = d.Id + " at " + where + ": " + d.GetMessage()
            };
        }

        private static string Mapped(SyntaxNode node)
        {
            var start = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
            return "\n#line " + (start.Line + 1) + " \"" + SourceName + "\"\n" + new string(' ', start.Character)
                + node.WithoutLeadingTrivia().ToFullString() + "\n";
        }

        /// <summary>
        /// Every loaded assembly with a file (newest version per name), with RevitAPI, RevitAPIUI and
        /// Newtonsoft.Json pinned to the ones this add-in uses. Unreadable files are skipped (rvt-mcp issue #38).
        /// </summary>
        private static List<MetadataReference> References()
        {
            var byName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location)) continue;
                    var name = assembly.GetName();
                    if (!byName.TryGetValue(name.Name, out var seen) || seen.GetName().Version < name.Version) byName[name.Name] = assembly;
                }
                catch
                {
                    // An assembly that cannot describe itself is not referenced.
                }
            }
            byName["RevitAPI"] = typeof(Document).Assembly;
            byName["RevitAPIUI"] = typeof(UIApplication).Assembly;
            byName["Newtonsoft.Json"] = typeof(JObject).Assembly;

            var paths = byName.Values.Select(a => a.Location).ToList();
#if NETCOREAPP
            // Facades user code compiles against but Revit may not have loaded yet.
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            foreach (var facade in new[] { "System.Runtime", "netstandard", "System.Collections", "System.Linq", "System.Runtime.Extensions" })
            {
                var path = Path.Combine(runtimeDir, facade + ".dll");
                if (!byName.ContainsKey(facade) && File.Exists(path)) paths.Add(path);
            }
#endif
            var references = new List<MetadataReference>();
            foreach (var path in paths)
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch (Exception ex) when (ex is BadImageFormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Protected add-in DLLs the CLR loads but Roslyn cannot read.
                }
            }
            return references;
        }

        private static Assembly Load(byte[] bytes)
        {
#if NETCOREAPP
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(CodeRunner).Assembly)
                ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            using (var stream = new MemoryStream(bytes)) return context.LoadFromStream(stream);
#else
            return Assembly.Load(bytes);
#endif
        }

        private static void RollBack(Transaction tx)
        {
            try
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            }
            catch
            {
                // Disposing the transaction rolls it back anyway.
            }
        }

        private static JToken ToJson(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token;
            try
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 16,
                    Converters = { new RevitObjectConverter() }
                });
                return JToken.FromObject(value, serializer);
            }
            catch
            {
                return new JValue(value.ToString());
            }
        }

        /// <summary>Writes Elements and ElementIds compactly instead of walking every Revit property.</summary>
        private sealed class RevitObjectConverter : JsonConverter
        {
            public override bool CanRead => false;

            public override bool CanConvert(Type objectType) =>
                typeof(Element).IsAssignableFrom(objectType) || objectType == typeof(ElementId);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is ElementId id)
                {
                    writer.WriteValue(RevitCompat.GetId(id));
                    return;
                }
                var element = (Element)value;
                string name;
                try { name = element.Name; } catch { name = null; }
                new JObject
                {
                    ["id"] = RevitCompat.GetId(element.Id),
                    ["name"] = name,
                    ["class"] = element.GetType().Name,
                    ["category"] = element.Category?.Name
                }.WriteTo(writer);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
                throw new NotSupportedException();
        }
    }
}
