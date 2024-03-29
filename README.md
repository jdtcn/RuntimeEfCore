# Roslyn & EF Core: runtime DbContext constructing

Entity Framework Core can generate model code and DbContext for an existing database using the console command `dotnet ef dbcontext scaffold`. Why don't we try generating a DbContext in runtime?

This sample project demonstrates how to:

1. Generate DbContext code using EF Core.
2. Compile it in memory using Roslyn.
3. Load the resulting assembly.
4. Create an instance of the generated DbContext.
5. Work with the database through dynamic DbContext.

# Prerequisites

We need NET 7.0 (or 3.1, 5.0, 6.0).

This program uses the MS SQL database, we need a connection string. However, the approach itself works for any database engine supported by EF Core (I tested sqlite and postregs).

Let's create a console application, add the necessary packages to it:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net7.0</TargetFramework>
  </PropertyGroup>
  
	<ItemGroup>
		<PackageReference Include="Microsoft.CodeAnalysis" Version="4.3.1" />
		<PackageReference Include="Microsoft.EntityFrameworkCore" Version="7.0.0" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="7.0.0" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.Proxies" Version="7.0.0" />
		<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="7.0.0" />
		<PackageReference Include="Bricelam.EntityFrameworkCore.Pluralizer" Version="1.0.0" />
	</ItemGroup>

</Project>
```

The code generator is in the package `Microsoft.EntityFrameworkCore.Design`. If you install this package through the package manager console, the following code will be added to your * .csproj:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="7.0.0" />
```

This code tells [1] that the package is needed only during development, and is not used in runtime. We will need it in runtime, so we need to import the package like this:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="7.0.0">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

# 1. Code generation

In the Entity Framework Core the code is generated by `IReverseEngineerScaffolder`:

```cs
interface IReverseEngineerScaffolder
{
    ScaffoldedModel ScaffoldModel(
        string connectionString, 

        // Select Tables, Schemes
        DatabaseModelFactoryOptions databaseOptions, 

        // Whether to use the database schema names directly
        ModelReverseEngineerOptions modelOptions, 

        // Represents the options to use while generating code for a model
        ModelCodeGenerationOptions codeOptions);
}
```

The easiest way to create an instance of this service is to create a Dependency Injection Container for it.

We place the dependencies necessary for the generator in the container:

```cs
IReverseEngineerScaffolder CreateMssqlScaffolder() => 
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

```

`IPluralizer` is optional. I use it to pluralize collection names.

<details>
<summary>PostgreSQL</summary>
<p>
```cs
private IReverseEngineerScaffolder CreatePostgreScaffolder() => 
    new ServiceCollection()
        .AddEntityFrameworkNpgsql()
        .AddLogging()
        .AddEntityFrameworkDesignTimeServices()
        .AddSingleton<LoggingDefinitions, NpgsqlLoggingDefinitions>()
        .AddSingleton<IRelationalTypeMappingSource, NpgsqlTypeMappingSource>()
        .AddSingleton<IAnnotationCodeGenerator, AnnotationCodeGenerator>()
        .AddSingleton<IDatabaseModelFactory, NpgsqlDatabaseModelFactory>()
        .AddSingleton<IProviderConfigurationCodeGenerator, NpgsqlCodeGenerator>()
        .AddSingleton<IScaffoldingModelFactory, RelationalScaffoldingModelFactory>()
        .AddSingleton<IPluralizer, Bricelam.EntityFrameworkCore.Design.Pluralizer>()
        .BuildServiceProvider()
        .GetRequiredService<IReverseEngineerScaffolder>();
```
</p>
</details>

<details>
<summary>sqlite</summary>
<p>
```cs
private IReverseEngineerScaffolder CreateSqliteScaffolder() => 
    new ServiceCollection()
        .AddEntityFrameworkSqlite()
        .AddLogging()
        .AddEntityFrameworkDesignTimeServices()
        .AddSingleton<LoggingDefinitions, SqliteLoggingDefinitions>()
        .AddSingleton<IRelationalTypeMappingSource, SqliteTypeMappingSource>()
        .AddSingleton<IAnnotationCodeGenerator, AnnotationCodeGenerator>()
        .AddSingleton<IDatabaseModelFactory, SqliteDatabaseModelFactory>()
        .AddSingleton<IProviderConfigurationCodeGenerator, SqliteCodeGenerator>()
        .AddSingleton<IScaffoldingModelFactory, RelationalScaffoldingModelFactory>()
        .AddSingleton<IPluralizer, Bricelam.EntityFrameworkCore.Design.Pluralizer>()
        .BuildServiceProvider()
        .GetRequiredService<IReverseEngineerScaffolder>();
```
</p>
</details>

Now you can get an instance of the code generator:

```cs
var scaffolder = CreateMssqlScaffolder();
```

We use the following settings for it:

```cs
// All tables and schemes
var dbOpts = new DatabaseModelFactoryOptions();

// Use the database schema names directly
var modelOpts = new ModelReverseEngineerOptions(); 

var codeGenOpts = new ModelCodeGenerationOptions()
{
    // Set namespaces
    RootNamespace = "TypedDataContext",
    ContextName = "DataContext",
    ContextNamespace = "TypedDataContext.Context",
    ModelNamespace = "TypedDataContext.Models",

    // We are not afraid of the connection string in the source code, 
    // because it will exist only in runtime
    SuppressConnectionStringWarning = true
};
```

Everything is ready, let's generate the database code

```cs
ScaffoldedModel scaffoldedModelSources =    
    scaffolder.ScaffoldModel(сonnectionString, dbOpts, modelOpts, codeGenOpts);
```

Execution result:

```cs
class ScaffoldedModel
{
    // DbContext code
    public virtual ScaffoldedFile ContextFile { get; set; }

    // Models code
    public virtual IList<ScaffoldedFile> AdditionalFiles { get; }
}
```

To use Lazy Loading, you need to add `UseLazyLoadingProxies ()` in the context file:

```cs
var contextFile = scaffoldedModelSources.ContextFile.Code
    .Replace(".UseSqlServer", ".UseLazyLoadingProxies().UseSqlServer");
```

Now that the source code is ready, let's compile it.

# 2. Compiling code with Roslyn

With Roslyn, compiling is very simple:

```cs
CSharpCompilation GenerateCode(List<string> sourceFiles)
{
    var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp8);
    var parsedSyntaxTrees = sourceFiles
        .Select(f => SyntaxFactory.ParseSyntaxTree(f, options));

    return CSharpCompilation.Create($"DataContext.dll",
        parsedSyntaxTrees,
        references: GetCompilationReferences(),
        options: new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release));
}
```

Specify references to the assemblies used:

```cs
List<MetadataReference> CompilationReferences()
{
    var refs = new List<MetadataReference>();

    // Reference all assemblies referenced by this program 
    var referencedAssemblies = Assembly.GetExecutingAssembly().GetReferencedAssemblies();
    refs.AddRange(referencedAssemblies.Select(a =>
        MetadataReference.CreateFromFile(Assembly.Load(a).Location)));

    // Add the missing ones needed to compile the assembly:
    refs.Add(MetadataReference.CreateFromFile(
        typeof(object).Assembly.Location));
    refs.Add(MetadataReference.CreateFromFile(
        Assembly.Load("netstandard, Version=2.0.0.0").Location));
    refs.Add(MetadataReference.CreateFromFile(
        typeof(System.Data.Common.DbConnection).Assembly.Location));
    refs.Add(MetadataReference.CreateFromFile(
        typeof(System.Linq.Expressions.Expression).Assembly.Location))
    
    // If we decided to use LazyLoading, we need to add one more assembly:
    // refs.Add(MetadataReference.CreateFromFile(
    //     typeof(ProxiesExtensions).Assembly.Location));
    
    return refs;
}
```

Let's compile our files:

```cs
MemoryStream peStream = new MemoryStream();
EmitResult emitResult = GenerateCode(sourceFiles).Emit(peStream);
```

If successful, `emitResult.Success` will be equal to` true`, and our assembly will be in `peStream`.

If something goes wrong, it will be easy to find the problem. All compilation errors and warnings will get into emitResult.

# 3. Loading compiled assembly

```cs
var assemblyLoadContext = new AssemblyLoadContext("DbContext", isCollectible);
            
var assembly = assemblyLoadContext.LoadFromStream(peStream);
```

I want to pay attention to the `isCollectible` parameter. It indicates whether the assembly can be unloaded and cleaned by the garbage collector. This useful feature appeared in NET Core 3 [2]

In our scenario, it will be useful to unload the assembly from memory when we finish working with the database. Make it simple:

```cs
assemblyLoadContext.Unload();
```

If LazyLoading is used, then EF Core will generate proxy objects for our entities, they will be loaded using DefaultLoadContext, and it is not marked as `collectible`. Since a NonCollectible assembly cannot reference a collectible assembly, we cannot make our `collectible` assembly at the same time using LazyLoading. Developers [3], [4] know about the problem, perhaps this will change in the future.

# 4. Using dynamic DbContext

Let's find the constructor in the assembly, and create an instance of our DbContext.

```cs
var type = assembly.GetType("TypedDataContext.Context.DataContext");

var constructor = type.GetConstructor(Type.EmptyTypes);

DbContext dynamicContext = (DbContext)constructor.Invoke(null);
```

For dynamic access, it is convenient to use the following extensions:

```cs
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
```

In these extensions, using Reflection, we get access to the typed `Set<>` method of our DbContext.

Now we will display in the console the names of the tables from the database, and the number of entries in each of them:

```cs
foreach (var entityType in dynamicContext.Model.GetEntityTypes())
{
    var items = (IQueryable<object>)dynamicContext.Query(entityType.Name);

    Console.Write($"Entity type: {entityType.ClrType.Name} ");
    Console.WriteLine($"contains {items.Count()} items");
}
```

# Application scenarios

This approach is convenient to use when creating auxiliary utilities in projects in which the database schema continues to change to avoid the need to manually recreate models and recompile the code.

# Summary

Using a small amount of code, you can dynamically create an EF Core DbContext in runtime. With the new NET Core feature - `collectible assemblies`, you can unload an assembly from memory, which helps to avoid memory leaks and performance problems.

# References

[1] [Package references (PackageReference) in project files](https://docs.microsoft.com/en-US/nuget/consume-packages/package-references-in-project-files)

[2] [Collectible assemblies in .NET Core 3.0](https://www.strathweb.com/2019/01/collectible-assemblies-in-net-core-3-0/)

[3] [Lazy loading proxy doesn't support entity inside collectible assembly #18272](https://github.com/dotnet/efcore/issues/18272)

[4] [Support Collectible Dynamic Assemblies #473](https://github.com/castleproject/Core/issues/473)

[5] [How to use and debug assembly unloadability in .NET Core](https://docs.microsoft.com/en-us/dotnet/standard/assembly/unloadability)