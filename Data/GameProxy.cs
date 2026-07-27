using Castle.DynamicProxy;
using ReflectionMagic;
using SpaceEditor.Rocks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SpaceEditor.Data;

public class InputActions
{
    public class InputActionInfo
    {
        public Guid Id;
        public string DisplayName { get; set; }
        public object DefinitionInstanceStub;
        public object DefinitionObjectBuilder;
    }
    public readonly Dictionary<Guid, InputActionInfo> Actions = new();

    public object[] DeserializationContexts = null!;

    public InputActionInfo? TryGetInputActionInfo(Guid id)
    {
        return this.Actions.GetValueOrDefault(id);
    }

    public InputActionInfo? TryGetInputActionInfo(object inputActionDefinitionStub)
    {
        var id = inputActionDefinitionStub.AsDynamic().Guid;
        return TryGetInputActionInfo((Guid)id);
    }
}

public class InputIds
{
    public readonly List<object> Analogs = new();
    public readonly List<object> Pointers = new();
    public readonly List<object> Digitals = new();

    public readonly List<Type> AnalogBuilders = new();
    public readonly List<Type> PointerBuilders = new();
    public readonly List<Type> DigitalBuilders = new();
    public IEnumerable<Type> Builders => this.AnalogBuilders.Concat(this.PointerBuilders).Concat(this.DigitalBuilders);

    public Dictionary<object, string> InputIdToDisplayName = new();

    public string GetDisplayName(object inputId)
    {
        return this.InputIdToDisplayName.GetValueOrDefault(inputId, "Unknown");
    }

    public IEnumerable<Type> GetBuilderTypes(Type targetType)
    {
        foreach (var builder in this.Builders)
        {
            if (targetType.IsAssignableFrom(builder))
                yield return builder;
        }
    }

    public List<Type> GetBuilderTypes(object kind)
    {
        var s = kind.ToString();
        var analogs = s.LastIndexOf("Analog");
        var pointers = s.LastIndexOf("Pointer");
        var digitals = s.LastIndexOf("Digital");

        var best = Math.Max(Math.Max(analogs, pointers), digitals);
        if (best == analogs)
        {
            return this.AnalogBuilders;
        }

        if (best == pointers)
        {
            return this.PointerBuilders;
        }

        if (best == digitals)
        {
            return this.DigitalBuilders;
        }

        throw new UnreachableException();
    }
}

public class BlockRecipe
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public Dictionary<string, int> Components { get; set; } = new();
}

public class BlockDefinitions
{
    public readonly Dictionary<string, BlockRecipe> Blocks = new(StringComparer.OrdinalIgnoreCase);
}

public class GameProxy
{
    public string BaseGamePath { get; }
    public string ContentPath { get; }
    public string BinsPath { get; }

    public Assembly MainAssembly { get; }

    public AsyncLazy<InputIds> InputIds { get; }
    public AsyncLazy<InputActions> InputActions { get; }
    public AsyncLazy<BlockDefinitions> BlockDefinitions { get; }

    public readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, object> RawDefinitions = new();

    public GameProxy(string baseGamePath)
    {
        this.BaseGamePath = baseGamePath;
        this.BinsPath = GameFacts.GetBinsPath(baseGamePath);
        this.ContentPath = GameFacts.GetContentPath(baseGamePath);

        var se2 = ReflectionRocks.GetLib(this.BinsPath, GameFacts.MainDll);
        this.MainAssembly = se2;

        var st = FindType("Keen.VRage.Library.Utils.Singleton");
        var mdt = FindType("Keen.VRage.Library.Reflection.MetadataManager");
        var md = st.AsDynamicType().GetInstance(mdt);
        md.PushContext(new[] { se2 });

        this.InputIds = new(LoadInputIds);
        this.InputActions = new(LoadInputActions);
        this.BlockDefinitions = new(LoadBlockDefinitions);
    }

    private object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        try
        {
            var prop = type.GetProperty(name);
            if (prop != null) return prop.GetValue(instance);
        }
        catch { }

        try
        {
            var field = type.GetField(name);
            if (field != null) return field.GetValue(instance);
        }
        catch { }

        return null;
    }

    private BlockDefinitions LoadBlockDefinitions()
    {
        var definitions = new BlockDefinitions();
        object syncObj = new object();

        var defContextType = FindType("DefinitionSerializationContext");
        var defsDir = GameFacts.GetDefinitionsPath(this.BaseGamePath);
        var validFiles = Directory.EnumerateFiles(defsDir, "*.def", SearchOption.AllDirectories).ToList();

        System.Diagnostics.Debug.WriteLine($"\n[GameProxy] ==========================================");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] Starting BlockDefinitions scan in: {defsDir}");

        // Pre-create the mock context proxy to pass into the file scanner
        var scanProxy = ProxyGen.CreateClassProxy(defContextType, new TryLocateDefinitionInterceptor(this, true, defContextType));

        int filesProcessed = 0;
        int filesSkippedByFastPath = 0;
        int filesFailedDeserialization = 0;
        int filesFailedReflection = 0;

        Parallel.ForEach(validFiles, file =>
        {
            try
            {
                string contentPreview = File.ReadAllText(file);
                if (!contentPreview.Contains("CubeBlock") && !contentPreview.Contains("Recipe"))
                {
                    lock (syncObj) { filesSkippedByFastPath++; }
                    return;
                }

                object defObject = null!;
                try
                {
                    // Pass the scanProxy explicitly so definitions can resolve references during startup scan
                    defObject = DeserializeFile(file, scanProxy);
                }
                catch
                {
                    lock (syncObj) { filesFailedDeserialization++; }
                    return;
                }

                if (defObject == null) return;

                System.Collections.IEnumerable definitionsList;
                try
                {
                    definitionsList = GetMemberValue(defObject, "Definitions") as System.Collections.IEnumerable ?? new[] { defObject };
                }
                catch
                {
                    definitionsList = new[] { defObject };
                }

                foreach (object def in definitionsList)
                {
                    if (def == null) continue;

                    try
                    {
                        var type = def.GetType();

                        try
                        {
                            var guidVal = GetMemberValue(def, "Guid") ?? GetMemberValue(def, "Id");
                            if (guidVal != null)
                            {
                                Guid defGuid = Guid.Empty;
                                if (guidVal is Guid g) defGuid = g;
                                else
                                {
                                    var innerGuid = GetMemberValue(guidVal, "Guid");
                                    if (innerGuid is Guid ig) defGuid = ig;
                                }

                                if (defGuid != Guid.Empty)
                                    this.RawDefinitions[defGuid] = def;
                            }
                        }
                        catch { }

                        var idObj = GetMemberValue(def, "Id");
                        if (idObj == null) continue;

                        string blockId = GetMemberValue(idObj, "SubtypeId")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(blockId)) continue;

                        string displayName = GetMemberValue(def, "DisplayName")?.ToString() ?? blockId;
                        var recipe = new BlockRecipe { Id = blockId, DisplayName = displayName };

                        var compsList = GetMemberValue(def, "Components") as System.Collections.IEnumerable;
                        if (compsList != null)
                        {
                            foreach (var comp in compsList)
                            {
                                if (comp == null) continue;
                                try
                                {
                                    string compId = GetMemberValue(comp, "SubtypeId")?.ToString() ?? "";
                                    object? countObj = GetMemberValue(comp, "Count");

                                    if (!string.IsNullOrEmpty(compId) && countObj != null)
                                    {
                                        int count = Convert.ToInt32(countObj);
                                        if (recipe.Components.ContainsKey(compId)) recipe.Components[compId] += count;
                                        else recipe.Components[compId] = count;
                                    }
                                }
                                catch { }
                            }
                        }

                        lock (syncObj)
                        {
                            definitions.Blocks[blockId] = recipe;
                            filesProcessed++;
                        }
                    }
                    catch
                    {
                        lock (syncObj) { filesFailedReflection++; }
                    }
                }
            }
            catch
            {
                lock (syncObj) { filesFailedDeserialization++; }
            }
        });

        System.Diagnostics.Debug.WriteLine($"[GameProxy] Scan Complete!");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] Blocks Registered: {definitions.Blocks.Count}");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] Fast-path skipped: {filesSkippedByFastPath}");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] Deserialization failures: {filesFailedDeserialization}");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] Reflection failures: {filesFailedReflection}");
        System.Diagnostics.Debug.WriteLine($"[GameProxy] ==========================================\n");

        return definitions;
    }

    public Type FindType(string typeName)
    {
        return ReflectionRocks.TryFindType(this.BinsPath, GameFacts.WellKnownGameBins, typeName) ??
               throw new Exception($"Type {typeName} not found");
    }

    public dynamic DeserializeFile(string filePath, params object[] services)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        string formatStr = "Json";
        byte[] header = new byte[4];
        int bytesRead = fs.Read(header, 0, 4);
        fs.Position = 0;

        if (bytesRead >= 4 && header[0] == 'V' && header[1] == 'R' && header[2] == '3' && header[3] == 'B')
        {
            formatStr = "Binary";
        }
        else
        {
            int b;
            do { b = fs.ReadByte(); } while (b == 0xEF || b == 0xBB || b == 0xBF || (b != -1 && char.IsWhiteSpace((char)b)));
            fs.Position = 0;

            if (b == '<') formatStr = "Xml";
            else if (b == '{' || b == '[') formatStr = "Json";
            else formatStr = "Binary";
        }

        return DeserializeObjectInternal(fs, formatStr, services);
    }

    public dynamic DeserializeObject(Stream content, params object[] services)
    {
        return DeserializeObjectInternal(content, "Json", services);
    }

    private static readonly Castle.DynamicProxy.ProxyGenerator ProxyGen = new();
    private object? _mockContextWithStubs;
    private object? _mockContextNoStubs;

    private Array BuildTypedServices(List<object> services)
    {
        var baseCscType = FindType("CustomSerializationContext");
        var genericCscType = ReflectionRocks.TryFindType(this.BinsPath, GameFacts.WellKnownGameBins, "CustomSerializationContext`1");
        var typedServices = Array.CreateInstance(baseCscType, services.Count);

        for (int i = 0; i < services.Count; i++)
        {
            var service = services[i];
            var type = service.GetType();

            if (type.Namespace != null && type.Namespace.StartsWith("Castle.Proxies"))
            {
                type = type.BaseType ?? type;
            }

            if (baseCscType.IsAssignableFrom(type))
            {
                typedServices.SetValue(service, i);
                continue;
            }

            if (genericCscType != null)
            {
                try
                {
                    var specificCscType = genericCscType.MakeGenericType(type);
                    var cscInstance = Activator.CreateInstance(specificCscType, service);
                    typedServices.SetValue(cscInstance, i);
                    continue;
                }
                catch { }
            }

            typedServices.SetValue(service, i);
        }

        return typedServices;
    }

    private dynamic DeserializeObjectInternal(Stream content, string formatStr, params object[] services)
    {
        var serviceList = new System.Collections.Generic.List<object>(services);
        var defContextType = FindType("DefinitionSerializationContext");

        bool hasDefContext = serviceList.Any(s => s.GetType() == defContextType || (s.GetType().BaseType != null && s.GetType().BaseType == defContextType));

        if (!hasDefContext && defContextType != null)
        {
            bool allowStubs = formatStr != "Binary";

            if (allowStubs)
            {
                _mockContextWithStubs ??= ProxyGen.CreateClassProxy(defContextType, new TryLocateDefinitionInterceptor(this, true, defContextType));
                serviceList.Add(_mockContextWithStubs);
            }
            else
            {
                _mockContextNoStubs ??= ProxyGen.CreateClassProxy(defContextType, new TryLocateDefinitionInterceptor(this, false, defContextType));
                serviceList.Add(_mockContextNoStubs);
            }
        }

        var typedServices = BuildTypedServices(serviceList);

        var format = Enum.Parse(FindType("SerializerFormat"), formatStr);
        using var scObj = (IDisposable)Activator.CreateInstance(FindType("SerializationContext"), content, "NoName.txt", typedServices)!;
        return FindType("SerializationHelper").AsDynamicType().DeserializeAbstract<object>(scObj, format);
    }

    public string SerializeObject(object instance, params object[] services)
    {
        var typedServices = BuildTypedServices(services.ToList());
        var format = Enum.Parse(FindType("SerializerFormat"), "Json");

        using var data = new MemoryStream();
        using var sc = (IDisposable)Activator.CreateInstance(FindType("SerializationContext"), data, "NoName.txt", typedServices)!;
        FindType("SerializationHelper").GetMethod("SerializeAbstract")!.MakeGenericMethod(typeof(object)).Invoke(null, [sc, instance, format]);

        return Encoding.UTF8.GetString(data.GetBuffer().AsSpan()[..(int)data.Length]);
    }

    private InputActions LoadInputActions()
    {
        var actions = new InputActions();
        var inputActionDefinitionType = FindType("InputActionDefinition");
        var actionsDir = GameFacts.GetActionsPath(this.BaseGamePath);

        Parallel.ForEach(Directory.EnumerateFiles(actionsDir), file =>
        {
            try
            {
                var def = DeserializeFile(file);
                Guid id = def.Guid;
                lock (actionsDir)
                {
                    actions.Actions.Add(id, new InputActions.InputActionInfo
                    {
                        Id = id,
                        DefinitionInstanceStub = DefinitionRocks.AllocateDefinitionStub(inputActionDefinitionType, id),
                        DefinitionObjectBuilder = DynamicHelper.Unwrap(def),
                        DisplayName = Path.GetFileNameWithoutExtension(file),
                    });
                }
            }
            catch { }
        });

        var defContextType = FindType("DefinitionSerializationContext");
        var mapGuidToDefinitionInstance = ProxyGen.CreateClassProxy
        (
            defContextType,
            new TryLocateDefinitionInterceptor(this, true, defContextType)
            {
                Actions = actions
            }
        );

        actions.DeserializationContexts = [mapGuidToDefinitionInstance];
        return actions;
    }

    private InputIds LoadInputIds()
    {
        var inputIds = new InputIds();
        var builderType = FindType("InputControlBuilder");
        var providerType = FindType("IPredefinedInputProvider");

        foreach (var assembly in GameFacts.WellKnownGameBins)
        {
            foreach (var provider in ReflectionRocks.GetLib(this.BaseGamePath, assembly).TryFindDerives(providerType))
            {
                var dynamicProvider = provider.AsDynamicType();
                foreach (var input in provider.GetFields(BindingFlags.Static | BindingFlags.Public))
                {
                    var kind = input.FieldType.Name switch
                    {
                        "PointerInput" => inputIds.Pointers,
                        "DigitalInput" => inputIds.Digitals,
                        "AnalogInput" => inputIds.Analogs,
                        _ => null
                    };

                    if (kind is null)
                        continue;

                    var inputId = DynamicHelper.Unwrap(input.GetValue(null).AsDynamic().Id);
                    kind.Add(inputId);

                    dynamicProvider.TryGetName(inputId, out string displayName);
                    inputIds.InputIdToDisplayName.Add(inputId, displayName);
                }
            }

            foreach (var builder in ReflectionRocks.GetLib(this.BaseGamePath, assembly).TryFindDerives(builderType))
            {
                inputIds.GetBuilderTypes(builder.Name).Add(builder);
            }
        }

        inputIds.AnalogBuilders.Sort((a, b) => a.Name.Length.CompareTo(b.Name.Length));
        inputIds.PointerBuilders.Sort((a, b) => a.Name.Length.CompareTo(b.Name.Length));
        inputIds.DigitalBuilders.Sort((a, b) => a.Name.Length.CompareTo(b.Name.Length));

        return inputIds;
    }

    public class TryLocateDefinitionInterceptor : IInterceptor
    {
        private readonly GameProxy _proxy;
        public InputActions? Actions { get; set; }
        public bool AllowStubs { get; set; }
        private readonly Type _targetType;

        public TryLocateDefinitionInterceptor(GameProxy proxy, bool allowStubs, Type targetType)
        {
            _proxy = proxy;
            AllowStubs = allowStubs;
            _targetType = targetType;
        }

        public void Intercept(IInvocation invocation)
        {
            var methodName = invocation.Method.Name;

            if (methodName == "get_ContextType" || methodName == "get_ServiceType" || methodName == "get_Type")
            {
                invocation.ReturnValue = _targetType;
                return;
            }

            if (methodName == "TryLocateDefinition" && invocation.Arguments.Length >= 3)
            {
                var id = invocation.Arguments[0] as Guid? ?? Guid.Empty;
                var type = invocation.Arguments[1] as Type;

                if (this.Actions != null)
                {
                    var info = this.Actions.TryGetInputActionInfo(id);
                    if (info != null)
                    {
                        invocation.Arguments[2] = info.DefinitionInstanceStub;
                        invocation.ReturnValue = true;
                        return;
                    }
                }

                if (_proxy.RawDefinitions.TryGetValue(id, out var rawDef))
                {
                    invocation.Arguments[2] = rawDef;
                    invocation.ReturnValue = true;
                    return;
                }

                if (this.AllowStubs && type != null)
                {
                    invocation.Arguments[2] = DefinitionRocks.AllocateDefinitionStub(type, id);
                    invocation.ReturnValue = true;
                }
                else
                {
                    invocation.Arguments[2] = null;
                    invocation.ReturnValue = false;
                }
                return;
            }

            if (invocation.Method.IsAbstract)
            {
                var returnType = invocation.Method.ReturnType;
                if (returnType == typeof(bool))
                    invocation.ReturnValue = false;
                else if (returnType.IsValueType && returnType != typeof(void))
                    invocation.ReturnValue = Activator.CreateInstance(returnType);
                else
                    invocation.ReturnValue = null;

                return;
            }

            invocation.Proceed();
        }
    }
}