using System.Reflection;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class BepInPluginAttribute : Attribute
    {
        public BepInPluginAttribute(string guid, string name, string version)
        {
            GUID = guid;
            Name = name;
            Version = version;
        }

        public string GUID { get; }
        public string Name { get; }
        public string Version { get; }
    }

    public static class Paths
    {
        public static string PluginPath { get; set; } = Path.Combine(Path.GetTempPath(), "MuvluvLLMMod.Integration");
    }
}

namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public ManualLogSource(string sourceName = "integration") => SourceName = sourceName;

        public string SourceName { get; }
        public Action<string>? OnLog { get; set; }
        public List<string> Messages { get; } = new();

        public void LogInfo(object data) => Write("INFO", data);
        public void LogWarning(object data) => Write("WARN", data);
        public void LogError(object data) => Write("ERROR", data);

        private void Write(string level, object data)
        {
            var message = $"{level}:{data}";
            lock (Messages) Messages.Add(message);
            OnLog?.Invoke(message);
        }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigDefinition
    {
        public ConfigDefinition(string section, string key)
        {
            Section = section;
            Key = key;
        }

        public string Section { get; }
        public string Key { get; }
    }

    public abstract class ConfigEntryBase
    {
        protected ConfigEntryBase(ConfigFile owner, ConfigDefinition definition)
        {
            Owner = owner;
            Definition = definition;
        }

        internal ConfigFile Owner { get; }
        public ConfigDefinition Definition { get; }
        public abstract object? BoxedValue { get; set; }
    }

    public sealed class ConfigEntry<T> : ConfigEntryBase
    {
        private T value;

        internal ConfigEntry(ConfigFile owner, ConfigDefinition definition, T value)
            : base(owner, definition) => this.value = value;

        public T Value
        {
            get => value;
            set
            {
                this.value = value;
                Owner.RaiseSettingChanged(this);
            }
        }

        public override object? BoxedValue
        {
            get => value;
            set => Value = (T)value!;
        }
    }

    public sealed class SettingChangedEventArgs : EventArgs
    {
        public SettingChangedEventArgs(ConfigEntryBase changedSetting) => ChangedSetting = changedSetting;

        public ConfigEntryBase ChangedSetting { get; }
    }

    public sealed class ConfigFile
    {
        private readonly Dictionary<(string Section, string Key), ConfigEntryBase> entries = new();

        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            if (entries.TryGetValue((section, key), out var existing))
                return (ConfigEntry<T>)existing;

            var entry = new ConfigEntry<T>(this, new ConfigDefinition(section, key), defaultValue);
            entries[(section, key)] = entry;
            return entry;
        }

        public void RaiseSettingChanged(ConfigEntryBase entry) =>
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(entry));

        public EventHandler<SettingChangedEventArgs>[] CaptureSettingHandlers() =>
            SettingChanged?.GetInvocationList()
                .Cast<EventHandler<SettingChangedEventArgs>>()
                .ToArray()
            ?? Array.Empty<EventHandler<SettingChangedEventArgs>>();
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type declaringType, string methodName)
        {
            DeclaringType = declaringType;
            MethodName = methodName;
        }

        public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes)
            : this(declaringType, methodName) => ArgumentTypes = argumentTypes;

        public Type DeclaringType { get; }
        public string MethodName { get; }
        public Type[]? ArgumentTypes { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPrefix : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPostfix : Attribute
    {
    }

    public sealed class PatchInfo
    {
        internal PatchInfo(IEnumerable<string> owners) => Owners = owners.ToArray();
        public IReadOnlyList<string> Owners { get; }
    }

    public sealed class Harmony
    {
        private static readonly object gate = new();
        private static readonly Dictionary<MethodInfo, HashSet<string>> owners = new();

        public Harmony(string id) => Id = id;

        public string Id { get; }
        public static string? SkipPatchTarget { get; set; }
        public static int UnpatchSelfCalls { get; private set; }

        public static void Reset()
        {
            lock (gate)
            {
                owners.Clear();
                SkipPatchTarget = null;
                UnpatchSelfCalls = 0;
            }
        }

        public void PatchAll(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                foreach (var patch in method.GetCustomAttributes<HarmonyPatch>(inherit: false))
                {
                    var target = AccessTools.Method(patch.DeclaringType, patch.MethodName, patch.ArgumentTypes);
                    if (target == null)
                        continue;
                    var fullLabel = patch.DeclaringType.FullName + "." + patch.MethodName;
                    var shortLabel = patch.DeclaringType.Name + "." + patch.MethodName;
                    if (string.Equals(SkipPatchTarget, fullLabel, StringComparison.Ordinal)
                        || string.Equals(SkipPatchTarget, shortLabel, StringComparison.Ordinal))
                        continue;
                    lock (gate)
                    {
                        if (!owners.TryGetValue(target, out var targetOwners))
                            owners[target] = targetOwners = new HashSet<string>(StringComparer.Ordinal);
                        targetOwners.Add(Id);
                    }
                }
            }
        }

        public void UnpatchSelf()
        {
            lock (gate)
            {
                foreach (var entry in owners.Values)
                    entry.Remove(Id);
                UnpatchSelfCalls++;
            }
        }

        public static PatchInfo? GetPatchInfo(MethodInfo? method)
        {
            if (method == null)
                return null;
            lock (gate)
                return owners.TryGetValue(method, out var targetOwners)
                    ? new PatchInfo(targetOwners)
                    : null;
        }
    }

    public static class AccessTools
    {
        public static MethodInfo? Method(Type type, string name, Type[]? argumentTypes = null)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return argumentTypes == null
                ? type.GetMethods(flags).FirstOrDefault(method => method.Name == name)
                : type.GetMethod(name, flags, binder: null, argumentTypes, modifiers: null);
        }
    }
}

namespace BepInEx.Unity.IL2CPP
{
    using BepInEx.Configuration;
    using BepInEx.Logging;
    using UnityEngine;

    public abstract class BasePlugin
    {
        protected BasePlugin()
        {
            Config = new ConfigFile();
            Log = new ManualLogSource(GetType().Name);
        }

        public ConfigFile Config { get; }
        public ManualLogSource Log { get; }
        public int AddComponentCalls { get; private set; }

        public virtual void Load()
        {
        }

        public virtual bool Unload() => false;

        protected T AddComponent<T>() where T : MonoBehaviour, new()
        {
            AddComponentCalls++;
            return UnityEngine.Object.AddComponent<T>();
        }
    }
}

namespace Il2CppSystem
{
    public sealed class Action
    {
        private readonly System.Action callback;

        public Action(System.Action callback) => this.callback = callback;

        public void Invoke() => callback();

        public static explicit operator Action(System.Action callback) => new(callback);
    }
}

namespace UnityEngine
{
    public enum FindObjectsInactive
    {
        Exclude,
        Include
    }

    public enum FindObjectsSortMode
    {
        None
    }

    public class Object
    {
        private static readonly object gate = new();
        private static readonly List<Object> objects = new();
        private static int nextInstanceId;

        protected Object()
        {
            lock (gate)
            {
                InstanceId = ++nextInstanceId;
                objects.Add(this);
            }
        }

        private bool destroyed;
        public int InstanceId { get; }

        public int GetInstanceID() => InstanceId;

        public static T AddComponent<T>() where T : MonoBehaviour, new() => new();

        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sortMode)
            where T : Object =>
            objects.OfType<T>().Where(item => !item.destroyed).ToArray();

        public static void Destroy(Object? value)
        {
            if (value == null)
                return;
            value.destroyed = true;
        }

        public static void ResetObjects()
        {
            lock (gate)
            {
                objects.Clear();
                nextInstanceId = 0;
            }
        }
    }

    public class MonoBehaviour : Object
    {
        public bool enabled { get; set; } = true;
    }

    public static class Time
    {
        public static float deltaTime { get; set; } = 0.5f;
    }

    public static class Application
    {
        private static readonly object gate = new();
        private static readonly List<Il2CppSystem.Action> handlers = new();

        public static bool ThrowOnRemove { get; set; }
        public static int AddCalls { get; private set; }
        public static int RemoveCalls { get; private set; }
        public static int CallbackCount
        {
            get { lock (gate) return handlers.Count; }
        }

        public static void add_quitting(Il2CppSystem.Action handler)
        {
            lock (gate)
            {
                handlers.Add(handler);
                AddCalls++;
            }
        }

        public static void remove_quitting(Il2CppSystem.Action handler)
        {
            lock (gate)
            {
                RemoveCalls++;
                if (ThrowOnRemove)
                    throw new InvalidOperationException("simulated native removal failure");
                var index = handlers.FindIndex(candidate => ReferenceEquals(candidate, handler));
                if (index < 0)
                    throw new InvalidOperationException("native delegate identity was not retained");
                handlers.RemoveAt(index);
            }
        }

        public static Il2CppSystem.Action[] SnapshotHandlers()
        {
            lock (gate) return handlers.ToArray();
        }

        public static void InvokeQuitting()
        {
            Il2CppSystem.Action[] snapshot;
            lock (gate) snapshot = handlers.ToArray();
            foreach (var handler in snapshot)
                handler.Invoke();
        }

        public static void Reset()
        {
            lock (gate)
            {
                handlers.Clear();
                ThrowOnRemove = false;
                AddCalls = 0;
                RemoveCalls = 0;
            }
        }
    }
}

namespace UnityEngine.InputSystem
{
    public enum Key
    {
        F2
    }

    public sealed class KeyControl
    {
        public bool wasPressedThisFrame { get; set; }
    }

    public sealed class Keyboard
    {
        public static Keyboard? current { get; set; }
        private readonly KeyControl f2 = new();

        public KeyControl this[Key key] => f2;
    }
}

namespace TMPro
{
    using UnityEngine;

    public class TMP_Text : MonoBehaviour
    {
        private string currentText = string.Empty;

        public static int SetterCalls { get; private set; }
        public static Action<TMP_Text, string>? BeforeStore { get; set; }

        public virtual string text
        {
            get => currentText;
            set
            {
                var incoming = value ?? string.Empty;
                MuvluvLLMMod.Patch.TranslateTmpSetter(this, ref incoming);
                BeforeStore?.Invoke(this, incoming);
                currentText = incoming;
                SetterCalls++;
            }
        }

        public static void ResetCounters() => SetterCalls = 0;
    }
}

namespace Assets.Api.Client
{
}

namespace Assets.GameUi.Scenario
{
    public sealed class ScenarioController
    {
        public void Refresh() { }
        public void Leave() { }
    }
}
