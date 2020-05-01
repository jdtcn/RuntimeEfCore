using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Diagnostics.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RuntimeEfCore
{
    class Program
    {
        static void Main(string[] args)
        {
            var connectionString = args.Length > 0
                ? args[0]
                : throw new Exception("Pass connection string as a first parameter");

            var scaffolder = CreateMssqlScaffolder();

            var dbOpts = new DatabaseModelFactoryOptions();
            var modelOpts = new ModelReverseEngineerOptions();
            var codeGenOpts = new ModelCodeGenerationOptions()
            {
                RootNamespace = "TypedDataContext",
                ContextName = "DataContext",
                ContextNamespace = "TypedDataContext.Context",
                ModelNamespace = "TypedDataContext.Models",
                SuppressConnectionStringWarning = true
            };

            var scaffoldedModelSources = scaffolder.ScaffoldModel(connectionString, dbOpts, modelOpts, codeGenOpts);
            var sourceFiles = new List<string> { scaffoldedModelSources.ContextFile.Code };
            sourceFiles.AddRange(scaffoldedModelSources.AdditionalFiles.Select(f => f.Code));

            using var peStream = new MemoryStream();

            var enableLazyLoading = false;
            var result = GenerateCode(sourceFiles, enableLazyLoading).Emit(peStream);

            if (!result.Success)
            {
                var failures = result.Diagnostics
                    .Where(diagnostic => diagnostic.IsWarningAsError || 
                                         diagnostic.Severity == DiagnosticSeverity.Error);

                var error = failures.FirstOrDefault();
                throw new Exception($"{error?.Id}: {error?.GetMessage()}");
            }

            var assemblyLoadContext = new AssemblyLoadContext("DbContext", isCollectible: !enableLazyLoading);

            peStream.Seek(0, SeekOrigin.Begin);
            var assembly = assemblyLoadContext.LoadFromStream(peStream);

            var type = assembly.GetType("TypedDataContext.Context.DataContext");
            _ = type ?? throw new Exception("DataContext type not found");

            var constr = type.GetConstructor(Type.EmptyTypes);
            _ = constr ?? throw new Exception("DataContext ctor not found");

            DbContext dynamicContext = (DbContext)constr.Invoke(null);
            var entityTypes = dynamicContext.Model.GetEntityTypes();

            Console.WriteLine($"Context contains {entityTypes.Count()} types");

            foreach (var entityType in dynamicContext.Model.GetEntityTypes())
            {
                var items = (IEnumerable<object>)dynamicContext.Query(entityType.Name);

                Console.WriteLine($"Entity type: {entityType.ClrType.Name} contains {items.Count()} items");
            }

            Console.ReadKey();

            if (!enableLazyLoading)
            {
                assemblyLoadContext.Unload();
            }
        }

        static IReverseEngineerScaffolder CreateMssqlScaffolder() =>
            new ServiceCollection()
               .AddEntityFrameworkSqlServer()
               .AddLogging()
               .AddEntityFrameworkDesignTimeServices()
               .AddSingleton<LoggingDefinitions, SqlServerLoggingDefinitions>()
               .AddSingleton<IRelationalTypeMappingSource, SqlServerTypeMappingSource>()
               .AddSingleton<IAnnotationCodeGenerator, AnnotationCodeGenerator>()
               .AddSingleton<IDatabaseModelFactory, SqlServerDatabaseModelFactory>()
               .AddSingleton<IProviderConfigurationCodeGenerator, SqlServerCodeGenerator>()
               .AddSingleton<IScaffoldingModelFactory, RelationalScaffoldingModelFactory>()
               .AddSingleton<IPluralizer, Bricelam.EntityFrameworkCore.Design.Pluralizer>()
               .BuildServiceProvider()
               .GetRequiredService<IReverseEngineerScaffolder>();


        static List<MetadataReference> CompilationReferences(bool enableLazyLoading)
        {
            var refs = new List<MetadataReference>();
            var referencedAssemblies = Assembly.GetExecutingAssembly().GetReferencedAssemblies();
            refs.AddRange(referencedAssemblies.Select(a => MetadataReference.CreateFromFile(Assembly.Load(a).Location)));

            refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            refs.Add(MetadataReference.CreateFromFile(Assembly.Load("netstandard, Version=2.0.0.0").Location));
            refs.Add(MetadataReference.CreateFromFile(typeof(System.Data.Common.DbConnection).Assembly.Location));
            refs.Add(MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location));

            if (enableLazyLoading)
            {
                refs.Add(MetadataReference.CreateFromFile(typeof(ProxiesExtensions).Assembly.Location));
            }

            return refs;
        }

        private static CSharpCompilation GenerateCode(List<string> sourceFiles, bool enableLazyLoading)
        {
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp8);

            var parsedSyntaxTrees = sourceFiles.Select(f => SyntaxFactory.ParseSyntaxTree(f, options));

            return CSharpCompilation.Create($"DataContext.dll",
                parsedSyntaxTrees,
                references: CompilationReferences(enableLazyLoading),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));
        }
    }

    public static class DynamicContextExtensions
    {
        public static IQueryable Query(this DbContext context, string entityName) =>
            context.Query(context.Model.FindEntityType(entityName).ClrType);

        static readonly MethodInfo SetMethod =
            typeof(DbContext).GetMethod(nameof(DbContext.Set), 1, Array.Empty<Type>()) ??
            throw new Exception($"Type not found: DbContext.Set");

        public static IQueryable Query(this DbContext context, Type entityType) =>
            (IQueryable)SetMethod.MakeGenericMethod(entityType)?.Invoke(context, null) ??
            throw new Exception($"Type not found: {entityType.FullName}");
    }
}
