using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline inspection only: never loads or executes either target assembly.
// Resolve metadata tokens to member signatures before comparing method bodies.
public static class CompareManagedBuilds
{
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(type => new[] { type }.Concat(Types(type.NestedTypes)));

    private static string Body(MethodDefinition method)
    {
        if (!method.HasBody) return "no body";
        var body = method.Body;
        int Index(Instruction instruction) => instruction == null ? -1 : body.Instructions.IndexOf(instruction);
        string Operand(object value) => value switch
        {
            Instruction instruction => "branch " + Index(instruction),
            Instruction[] instructions => "switch " + string.Join(",", instructions.Select(Index)),
            MemberReference member => member.FullName,
            VariableDefinition local => "local " + local.Index,
            ParameterDefinition parameter => "argument " + parameter.Index,
            float number => BitConverter.SingleToInt32Bits(number).ToString("X8"),
            double number => BitConverter.DoubleToInt64Bits(number).ToString("X16"),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? ""
        };
        return "init=" + body.InitLocals + ";stack=" + body.MaxStackSize + "\n" +
            string.Join("\n", body.Variables.Select(local => local.VariableType.FullName)) + "\n" +
            string.Join("\n", body.Instructions.Select(instruction => instruction.OpCode.Name + " " + Operand(instruction.Operand))) + "\n" +
            string.Join("\n", body.ExceptionHandlers.Select(handler => handler.HandlerType + " " + handler.CatchType?.FullName + " " +
                Index(handler.TryStart) + ":" + Index(handler.TryEnd) + " " + Index(handler.HandlerStart) + ":" + Index(handler.HandlerEnd) + " " + Index(handler.FilterStart)));
    }

    public static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: CompareManagedBuilds OLD.dll NEW.dll REPORT.json"); return 2; }
        using var before = AssemblyDefinition.ReadAssembly(args[0]);
        using var after = AssemblyDefinition.ReadAssembly(args[1]);
        string Key(MethodDefinition method) => method.FullName + "`" + method.GenericParameters.Count;
        var oldMethods = Types(before.MainModule.Types).SelectMany(type => type.Methods).ToDictionary(Key);
        var newMethods = Types(after.MainModule.Types).SelectMany(type => type.Methods).ToDictionary(Key);
        var changed = new List<string>();
        int equal = 0;
        foreach (var entry in newMethods)
        {
            if (!oldMethods.TryGetValue(entry.Key, out var original)) continue;
            if (Body(original) == Body(entry.Value)) equal++; else changed.Add(entry.Key);
        }
        var report = new
        {
            old_version = before.Name.Version.ToString(), new_version = after.Name.Version.ToString(),
            old_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[0]))),
            new_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]))),
            equal_bodies = equal, changed_bodies = changed,
            added = newMethods.Keys.Except(oldMethods.Keys).ToArray(), removed = oldMethods.Keys.Except(newMethods.Keys).ToArray(),
            limitation = "Compares resolved IL, locals, stack and exception regions. Does not prove equivalence of changed dependencies, fields, types or runtime behavior."
        };
        File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{before.Name.Name}: {equal} identical resolved bodies; {changed.Count} changed; report={args[2]}");
        return 0;
    }
}
