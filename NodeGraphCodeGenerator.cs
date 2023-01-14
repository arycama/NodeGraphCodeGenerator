using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

namespace NodeGraphCodeGenerator
{
    [Generator]
    public class NodeGraphCodeGenerator : ISourceGenerator
    {
        private Compilation compilation;

        void ISourceGenerator.Initialize(GeneratorInitializationContext context) { }

        void ISourceGenerator.Execute(GeneratorExecutionContext context)
        {
            var classes = context.Compilation.SyntaxTrees.SelectMany(syntaxTree => syntaxTree.GetRoot().DescendantNodes()).OfType<ClassDeclarationSyntax>();
            compilation = context.Compilation;

            foreach (var classDeclarationSyntax in classes)
            {
                var fields = classDeclarationSyntax.Members.OfType<FieldDeclarationSyntax>();
                var inputFields = fields.Where(field => field.AttributeLists.Any(attributes => attributes.Attributes.Any(attribute => attribute.Name.ToString() == "Input" || attribute.Name.ToString() == "InputNoUpdate")));
                var inputArrayFields = fields.Where(field => field.AttributeLists.Any(attributes => attributes.Attributes.Any(attribute => attribute.Name.ToString() == "InputArray")));
                var outputFields = fields.Where(field => field.AttributeLists.Any(attributes => attributes.Attributes.Any(attribute => attribute.Name.ToString() == "Output")));

                if (inputFields.Count() == 0 && inputArrayFields.Count() == 0 && outputFields.Count() == 0)
                    continue;

                var sb = new IndentedStringBuilder();

                sb.AppendLine("// This file is auto-generated, do not edit.");
                sb.AppendLine(string.Empty);

                // Todo: create usings automatically
                sb.AppendLine("using System;");
                sb.AppendLine("using System.Collections.Generic;");
                sb.AppendLine("using UnityEngine;");
                sb.AppendLine("using UnityEngine.Rendering;");
                sb.AppendLine(string.Empty);

                IndentedStringBuilder.IndentScope? namespaceScope = null;
                if (classDeclarationSyntax.Parent is NamespaceDeclarationSyntax namespaceDeclarationSyntax)
                {
                    sb.AppendLine($"namespace {namespaceDeclarationSyntax.Name}");
                    namespaceScope = sb.AppendBlock();
                }

                var semanticModel = compilation.GetSemanticModel(classDeclarationSyntax.SyntaxTree);
                var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax);

                if(typeSymbol.IsGenericType)
                {
                    var typeString = string.Join(", ", typeSymbol.TypeParameters);
                    sb.AppendLine($"public partial class {classDeclarationSyntax.Identifier}<{typeString}>");
                }
                else
                {
                    sb.AppendLine($"public partial class {classDeclarationSyntax.Identifier}");
                }

                using (sb.AppendBlock())
                {
                    // Add serialized references to inputNode and inputNode.fieldName
                    foreach (var inputField in inputFields)
                    {
                        foreach (var variable in inputField.Declaration.Variables)
                        {
                            // Add serialized reference to input node
                            sb.AppendLine($"[SerializeField, HideInInspector] private BaseNode {variable.Identifier}Node;");

                            // Add serialized reference to field name of input node
                            sb.AppendLine($"[SerializeField, HideInInspector] private string {variable.Identifier}FieldName;");
                        }
                    }

                    // Add serialized inputArrays
                    foreach (var inputField in inputArrayFields)
                    {
                        foreach (var variable in inputField.Declaration.Variables)
                        {
                            sb.AppendLine($"[SerializeField, HideInInspector] private SerializableTuple<BaseNode, string>[] {variable.Identifier}Array = new SerializableTuple<BaseNode, string>[0];");
                        }
                    }

                    // Generate GetValueClass method (If there is at least one class field)
                    GenerateOutputClassFieldMethods(outputFields, sb);

                    // Generate GetValueStruct method (If there is at least one value field)
                    GenerateOutputValueFieldMethods(outputFields, sb);

                    // Generate SetConnectedNode (If there is at least one inputField)
                    GenerateInputFieldMethods(inputFields, inputArrayFields, sb);

                    // Generate GetArrayLength method
                    GenerateInputArrayFieldMethods(inputArrayFields, sb);

                    GenerateGetNodeCount(inputFields, inputArrayFields, sb);
                    GenerateGetNodeAtIndex(inputFields, inputArrayFields, sb);
                }

                if (namespaceScope != null)
                    namespaceScope.Value.Dispose();

                sb.AppendLine(string.Empty);

                // add the generated implementation to the compilation
                SourceText sourceText = SourceText.From(sb.ToString(), Encoding.UTF8);
                context.AddSource($"{classDeclarationSyntax.Identifier}.generated.cs", sourceText);
            }
        }

        private void GenerateOutputClassFieldMethods(IEnumerable<FieldDeclarationSyntax> outputFields, IndentedStringBuilder sb)
        {
            GenerateGetValueClassMethod(outputFields, sb);
        }

        private void GenerateGetValueClassMethod(IEnumerable<FieldDeclarationSyntax> outputFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override T GetValueClass<T>(string fieldName) where T : class");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (fieldName)");
                using (sb.AppendBlock())
                {
                    var outputClassFields = outputFields.Where(outputField => IsClass(outputField));
                    foreach (var field in outputClassFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    return {variable.Identifier} as T;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine($"return base.GetValueClass<T>(fieldName);");
            }

            sb.AppendLine(string.Empty);
        }

        public ITypeSymbol GetType(FieldDeclarationSyntax field)
        {
            return compilation.GetSemanticModel(field.SyntaxTree).GetTypeInfo(field.Declaration.Type).Type;
        }

        public bool IsClass(FieldDeclarationSyntax field)
        {
            return GetType(field).TypeKind == TypeKind.Class;
        }

        private void GenerateOutputValueFieldMethods(IEnumerable<FieldDeclarationSyntax> outputFields, IndentedStringBuilder sb)
        {
            if (!outputFields.Any(outputValueField => !IsClass(outputValueField))) return;

            GenerateGetValueStructMethod(outputFields, sb);
        }

        private void GenerateGetValueStructMethod(IEnumerable<FieldDeclarationSyntax> outputFields, IndentedStringBuilder sb)
        {
            // Need to group the output fields by type
            var outputTypes = new Dictionary<string, List<FieldDeclarationSyntax>>();
            foreach (var field in outputFields)
            {
                if (IsClass(field))
                    continue;

                if (!outputTypes.TryGetValue(GetType(field).Name, out var list))
                {
                    list = new List<FieldDeclarationSyntax>();
                    outputTypes.Add(GetType(field).Name, list);
                }

                list.Add(field);
            }

            // Now for each type, generate a method
            foreach (var type in outputTypes)
            {
                sb.AppendLine($"public override {type.Key} GetValue{type.Key}(string fieldName)");
                using (sb.AppendBlock())
                {
                    sb.AppendLine("switch (fieldName)");
                    using (sb.AppendBlock())
                    {
                        foreach (var field in type.Value)
                        {
                            foreach (var variable in field.Declaration.Variables)
                            {
                                sb.AppendLine($"case \"{variable.Identifier}\":");
                                sb.AppendLine($"        return {variable.Identifier};");
                                sb.AppendLine($"    break;");
                            }
                        }
                    }

                    sb.AppendLine(string.Empty);

                    sb.AppendLine($"return base.GetValue{type.Key}(fieldName);");
                }
            }

            sb.AppendLine(string.Empty);
        }

        private void GenerateInputFieldMethods(IEnumerable<FieldDeclarationSyntax> inputFields, IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            if (inputFields.Count() <= 0) return;

            GenerateSetPortConnectionMethod(inputFields, sb);

            // GetConnectedNode
            GenerateGetConnectedNodeMethod(inputFields, sb);

            // UpdateValues
            GenerateUpdateValuesMethod(inputFields, inputArrayFields, sb);

            sb.AppendLine(string.Empty);
        }

        private void GenerateSetPortConnectionMethod(IEnumerable<FieldDeclarationSyntax> inputFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override void SetConnectedNode(string inputField, BaseNode outputNode, string outputField)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (inputField)");
                using (sb.AppendBlock())
                {
                    foreach (var field in inputFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    {variable.Identifier}Node = outputNode;");
                            sb.AppendLine($"    {variable.Identifier}FieldName = outputField;");
                            sb.AppendLine("    return;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine("base.SetConnectedNode(inputField, outputNode, outputField);");
            }

            sb.AppendLine(string.Empty);
        }

        private void GenerateGetConnectedNodeMethod(IEnumerable<FieldDeclarationSyntax> inputFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override BaseNode GetConnectedNode(string fieldName, out string connectedFieldName)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (fieldName)");
                using (sb.AppendBlock())
                {
                    foreach (var field in inputFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    connectedFieldName = {variable.Identifier}FieldName;");
                            sb.AppendLine($"    return {variable.Identifier}Node;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine("return base.GetConnectedNode(fieldName, out connectedFieldName);");
            }

            sb.AppendLine(string.Empty);
        }

        private void GenerateUpdateValuesMethod(IEnumerable<FieldDeclarationSyntax> inputFields, IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override void UpdateValues()");
            using (sb.AppendBlock())
            {
                foreach (var field in inputFields)
                {
                    var type = GetType(field);
                    var generic = field.Declaration.Type as GenericNameSyntax;

                    foreach (var variable in field.Declaration.Variables)
                    {
                        sb.AppendLine($"if ({variable.Identifier}Node != null)");
                        using (sb.AppendBlock())
                        {
                            // Need to use a different method depending on whether target is struct or class
                            if (type.TypeKind == TypeKind.Class)
                            {
                                var typeName = type.SpecialType == SpecialType.System_Object ? "object" : type.Name;

                                if (generic != null)
                                {
                                    var typeArg = generic.TypeArgumentList.Arguments[0];
                                    sb.AppendLine($"{variable.Identifier} = {variable.Identifier}Node.GetValueClass<{typeName}<{typeArg}>>({variable.Identifier}FieldName);");

                                }
                                else
                                {
                                    sb.AppendLine($"{variable.Identifier} = {variable.Identifier}Node.GetValueClass<{typeName}>({variable.Identifier}FieldName);");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"{variable.Identifier} = {variable.Identifier}Node.GetValue{type.Name}({variable.Identifier}FieldName);");
                            }
                        }

                        sb.AppendLine(string.Empty);
                    }
                }

                // Arrays
                foreach (var field in inputArrayFields)
                {
                    var type = (GetType(field) as IArrayTypeSymbol).ElementType;

                    foreach (var variable in field.Declaration.Variables)
                    {
                        var fieldName = variable.Identifier;

                        sb.AppendLine($"if ({fieldName} == null)");
                        sb.AppendLine($"    {fieldName} = new {type.Name}[{fieldName}Array.Length];");
                        sb.AppendLine(string.Empty);

                        sb.AppendLine($"for (var i = 0; i < {fieldName}.Length; i++)");
                        using (sb.AppendBlock())
                        {
                            sb.AppendLine($"{fieldName}Array[i].Item1.UpdateValues();");
                            if (type.TypeKind == TypeKind.Class)
                            {
                                sb.AppendLine($"{fieldName}[i] = {fieldName}Array[i].Item1.GetValueClass<{type.Name}>({fieldName}Array[i].Item2);");
                            }
                            else if (type.TypeKind == TypeKind.Struct)
                            {
                                sb.AppendLine($"{fieldName}[i] = {fieldName}Array[i].Item1.GetValue{type.Name}({fieldName}Array[i].Item2);");
                            }
                        }

                        sb.AppendLine(string.Empty);
                    }
                }

                sb.AppendLine("base.UpdateValues();");
            }
        }

        private void GenerateInputArrayFieldMethods(IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            if (inputArrayFields.Count() <= 0) return;

            GenerateGetArraySizeMethod(inputArrayFields, sb);
            GenerateSetConnectedNodeArrayMethod(inputArrayFields, sb);
            GenerateGetConnectedNodeArrayMethod(inputArrayFields, sb);
        }

        private void GenerateGetArraySizeMethod(IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override int GetArraySize(string fieldName)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (fieldName)");
                using (sb.AppendBlock())
                {
                    foreach (var field in inputArrayFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    return {variable.Identifier}Array.Length;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine("return base.GetArraySize(fieldName);");
            }
        }

        private void GenerateSetConnectedNodeArrayMethod(IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override void SetConnectedNodeArray(string inputField, BaseNode outputNode, string outputField, int arrayIndex)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (inputField)");
                using (sb.AppendBlock())
                {
                    foreach (var field in inputArrayFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    if (GetArraySize(inputField) <= arrayIndex) Array.Resize(ref {variable.Identifier}Array, arrayIndex + 1);");
                            sb.AppendLine($"    {variable.Identifier}Array[arrayIndex] = new SerializableTuple<BaseNode, string>(outputNode, outputField);");
                            sb.AppendLine($"    Array.Resize(ref {variable.Identifier}Array, Array.FindLastIndex({variable.Identifier}Array, element => element.Item1 != null) + 1);");
                            sb.AppendLine($"    return;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine("base.SetConnectedNodeArray(inputField, outputNode, outputField, arrayIndex);");
            }
        }

        private void GenerateGetConnectedNodeArrayMethod(IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            sb.AppendLine("public override BaseNode GetConnectedNodeArray(string fieldName, int arrayIndex, out string connectedFieldName)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (fieldName)");
                using (sb.AppendBlock())
                {
                    foreach (var field in inputArrayFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case \"{variable.Identifier}\":");
                            sb.AppendLine($"    connectedFieldName = {variable.Identifier}Array[arrayIndex].Item2;");
                            sb.AppendLine($"    return {variable.Identifier}Array[arrayIndex].Item1;");
                        }
                    }
                }

                sb.AppendLine(string.Empty);

                sb.AppendLine("return base.GetConnectedNodeArray(fieldName, arrayIndex, out connectedFieldName);");
            }
        }

        private void GenerateGetNodeCount(IEnumerable<FieldDeclarationSyntax> inputFields, IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            sb.AppendLine($"public override int GetNodeCount()");
            using (sb.AppendBlock())
            {
                sb.AppendLine($"return {inputFields.Count()} + base.GetNodeCount();");
            }

            sb.AppendLine(string.Empty);

            sb.AppendLine($"public override int GetNodeArrayCount()");
            using (sb.AppendBlock())
            {
                sb.AppendLine($"return {inputArrayFields.Count()} + base.GetNodeArrayCount();");
            }
            sb.AppendLine(string.Empty);

            sb.AppendLine("public override int GetNodeArrayElementCount(int index)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (index)");
                using (sb.AppendBlock())
                {
                    var index = 0;
                    foreach (var field in inputArrayFields)
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case {index++}:");
                            sb.AppendLine($"    return {variable.Identifier}Array.Length;");
                        }
                    }
                }

                sb.AppendLine("return base.GetNodeArrayElementCount(index);");
            }
        }

        private void GenerateGetNodeAtIndex(IEnumerable<FieldDeclarationSyntax> inputFields, IEnumerable<FieldDeclarationSyntax> inputArrayFields, IndentedStringBuilder sb)
        {
            if (inputFields.Count() > 0)
            {
                sb.AppendLine("public override BaseNode GetNodeAtIndex(int index)");
                using (sb.AppendBlock())
                {
                    sb.AppendLine("switch (index)");
                    using (sb.AppendBlock())
                    {
                        var index = 0;
                        foreach (var field in inputFields)
                        {
                            var inputNoUpdate = field.AttributeLists.Any(attributes => attributes.Attributes.Any(attribute => attribute.Name.ToString() == "InputNoUpdate"));

                            foreach (var variable in field.Declaration.Variables)
                            {
                                sb.AppendLine($"case {index++}:");

                                // A special attribute can be used in certain situations to avoid an automatic update of connected nodes
                                if (inputNoUpdate)
                                {
                                    // For now just return null and have the caller check it
                                    sb.AppendLine($"    return null;");
                                }
                                else
                                {
                                    sb.AppendLine($"    return {variable.Identifier}Node;");
                                }
                            }
                        }
                    }

                    sb.AppendLine(string.Empty);
                    sb.AppendLine($"return base.GetNodeAtIndex(index - {inputFields.Count()});");
                }
            }

            // Array
            sb.AppendLine("public override BaseNode GetNodeAtArrayIndex(int arrayIndex, int elementIndex)");
            using (sb.AppendBlock())
            {
                sb.AppendLine("switch (arrayIndex)");
                using (sb.AppendBlock())
                {
                    var index = 0;
                    foreach (var field in inputArrayFields)
                    {
                        var inputNoUpdate = field.AttributeLists.Any(attributes => attributes.Attributes.Any(attribute => attribute.Name.ToString() == "InputNoUpdate"));

                        foreach (var variable in field.Declaration.Variables)
                        {
                            sb.AppendLine($"case {index++}:");

                            // A special attribute can be used in certain situations to avoid an automatic update of connected nodes
                            if (inputNoUpdate)
                            {
                                // For now just return null and have the caller check it
                                sb.AppendLine($"    return null;");
                            }
                            else
                            {
                                sb.AppendLine($"    return {variable.Identifier}Array[elementIndex]?.Item1;");
                            }
                        }
                    }
                }

                sb.AppendLine(string.Empty);
                sb.AppendLine($"return base.GetNodeAtArrayIndex(arrayIndex, elementIndex- {inputArrayFields.Count()});");
            }
        }
    }
}
