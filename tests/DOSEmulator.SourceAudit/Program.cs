// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Syntax-aware inventory: comments do not alter executable tokens, strings, directives or assembler bytes.
string root = Path.GetFullPath(args[0]);
if (args.Length == 2 && args[1] == "--state-inventory")
{
    var inventory = new List<object>();
    foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(c => c is "obj" or "bin") && !p.EndsWith(".State.g.cs")))
    {
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        foreach (var type in syntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            string name = string.Join(".", type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(v => v.Identifier.Text).Append(type.Identifier.Text));
            string ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
            foreach (var call in type.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(v => v.Ancestors().OfType<TypeDeclarationSyntax>().First() == type &&
                    v.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == "ScheduleAfter"))
                inventory.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), ns, type = name,
                    category = "schedule", name = "event", valueType = "", modifiers = "",
                    start = call.SpanStart, end = call.Span.End, receiver = ((MemberAccessExpressionSyntax)call.Expression).Expression.ToString(),
                    delay = call.ArgumentList.Arguments[0].ToString(), callback = call.ArgumentList.Arguments[1].ToString(),
                    arguments = call.ArgumentList.Arguments.Count });
            if (type is ClassDeclarationSyntax declaration && declaration.ParameterList is { } primary)
                foreach (var parameter in primary.Parameters)
                    inventory.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), ns, type = name,
                        category = "primary", name = parameter.Identifier.Text, valueType = parameter.Type!.ToString(), modifiers = "" });
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                foreach (var value in field.Declaration.Variables)
                    inventory.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), ns, type = name,
                        category = "field", name = value.Identifier.Text, valueType = field.Declaration.Type.ToString(),
                        modifiers = field.Modifiers.ToString(), initial = value.Initializer?.Value.ToString() });
            foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>()
                .Where(v => v.AccessorList is not null && v.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null)))
                inventory.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), ns, type = name,
                    category = "property", name = property.Identifier.Text, valueType = property.Type.ToString(),
                    modifiers = property.Modifiers.ToString(), initial = property.Initializer?.Value.ToString(),
                    writable = property.AccessorList!.Accessors.Any(a => a.Keyword.Text is "set" or "init") });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Length == 3 && args[1] == "--compare")
{
    string baseline = Path.GetFullPath(args[2]);
    var comparison = new List<object>();
    foreach (string path in new[] { "src", "tests" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
    {
        string relative = Path.GetRelativePath(root, path), old = Path.Combine(baseline, relative);
        if (!File.Exists(old)) { comparison.Add(new { file = relative, classification = "added" }); continue; }
        string before = File.ReadAllText(old), after = File.ReadAllText(path);
        if (before == after) continue;
        var a = CSharpSyntaxTree.ParseText(before).GetRoot().DescendantTokens().Select(t => (t.RawKind, t.Text));
        var b = CSharpSyntaxTree.ParseText(after).GetRoot().DescendantTokens().Select(t => (t.RawKind, t.Text));
        comparison.Add(new { file = relative, classification = a.SequenceEqual(b) ? "documentation-only" : "executable-change" });
    }
    Console.WriteLine(JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Length == 3 && args[1] == "--apply")
{
    var plan = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, string>>>(File.ReadAllText(args[2]))!;
    var pending = new List<(string Path, string Text)>();
    foreach (var filePlan in plan)
    {
        string path = Path.GetFullPath(filePlan.Key, root);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Comment plan escaped the repository.");
        string text = File.ReadAllText(path); var before = CSharpSyntaxTree.ParseText(text).GetRoot();
        var tokens = before.DescendantTokens().Select(t => (t.RawKind, t.Text)).ToArray();
        foreach (var edit in filePlan.Value.OrderByDescending(e => e.Key))
        {
            var node = before.DescendantNodes().First(n => n.SpanStart == edit.Key && n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax);
            int lineStart = text.LastIndexOf('\n', Math.Max(0, edit.Key - 1)) + 1;
            string prefix = text[lineStart..edit.Key];
            string indent = new(' ', node.Ancestors().Count(n => n is TypeDeclarationSyntax or BlockSyntax) * 4);
            bool ownLine = string.IsNullOrWhiteSpace(prefix);
            if (ownLine) indent = prefix;
            string escaped = edit.Value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            string comment = node is LocalFunctionStatementSyntax ? "// " + edit.Value : "/// <summary>" + escaped + "</summary>";
            string insertion = (ownLine ? "" : "\n" + indent) + comment + "\n" + indent;
            text = text.Insert(edit.Key, insertion);
        }
        var after = CSharpSyntaxTree.ParseText(text).GetRoot();
        if (!tokens.SequenceEqual(after.DescendantTokens().Select(t => (t.RawKind, t.Text)))) throw new InvalidOperationException("Comment edit changed executable tokens: " + path);
        pending.Add((path, text));
    }
    // Validate every token sequence before committing any planned source edit.
    foreach (var item in pending) File.WriteAllText(item.Path, item.Text);
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, files = plan.Count, declarations = plan.Values.Sum(p => p.Count), executableTokensUnchanged = true }));
    return;
}
var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
    .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(c => c is "obj" or "bin"));
var rows = new List<object>();
var problems = new List<string>();
int declarations = 0, fileCount = 0;
foreach (string file in files)
{
    fileCount++;
    string text = File.ReadAllText(file); var tree = CSharpSyntaxTree.ParseText(text); var syntax = tree.GetRoot();
    if (!text[..Math.Min(1800, text.Length)].Contains("SPDX-License-Identifier:") && !text[..Math.Min(1800, text.Length)].Contains("Licensed under the Apache License"))
        problems.Add(Path.GetRelativePath(root, file) + ": missing source license header");
    foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)) problems.Add(file + ": " + error);
    foreach (var node in syntax.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax))
    {
        string name = node switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
            OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
            ConversionOperatorDeclarationSyntax c => "conversion " + c.Type,
            LocalFunctionStatementSyntax f => f.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            _ => "unknown"
        };
        string leading = node.GetLeadingTrivia().ToFullString();
        bool documented = leading.Contains("///") || leading.Contains("//") || leading.Contains("/*");
        declarations++;
        if (!documented) problems.Add(Path.GetRelativePath(root, file) + ": " + name + " lacks a function/property comment");
        string signature = node.ToString().Split('{')[0].Split("=>")[0].Trim();
        rows.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), name,
            type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text,
            kind = node.Kind().ToString(), start = node.SpanStart, line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
            documented,
            signature, body = node.ToString() });
    }
}
if (args.Contains("--check"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { passed = problems.Count == 0, files = fileCount, declarations, problems }, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = problems.Count == 0 ? 0 : 1;
    return;
}
Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
