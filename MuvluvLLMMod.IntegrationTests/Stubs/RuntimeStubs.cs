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
        public sealed record PatchApplication(string Owner, string PatchMethod, string Target);

        private static readonly object gate = new();
        private static readonly Dictionary<MethodBase, HashSet<string>> owners = new();
        private static readonly List<PatchApplication> applications = new();

        public Harmony(string id) => Id = id;

        public string Id { get; }
        public static string? SkipPatchTarget { get; set; }
        public static bool BlockPatchAll { get; set; }
        public static TaskCompletionSource<bool> PatchAllEntered { get; private set; } =
            NewSignal();
        public static TaskCompletionSource<bool> PatchAllRelease { get; private set; } =
            NewSignal();
        public static bool BlockAfterPatchAllPublication { get; set; }
        public static TaskCompletionSource<bool> PatchAllPublished { get; private set; } =
            NewSignal();
        public static TaskCompletionSource<bool> PatchAllPostPublishRelease { get; private set; } =
            NewSignal();
        public static int UnpatchSelfCalls { get; private set; }

        public static IReadOnlyList<PatchApplication> SnapshotApplications()
        {
            lock (gate)
                return applications.ToArray();
        }

        public static void Reset()
        {
            lock (gate)
            {
                owners.Clear();
                applications.Clear();
                SkipPatchTarget = null;
                BlockPatchAll = false;
                PatchAllEntered = NewSignal();
                PatchAllRelease = NewSignal();
                BlockAfterPatchAllPublication = false;
                PatchAllPublished = NewSignal();
                PatchAllPostPublishRelease = NewSignal();
                UnpatchSelfCalls = 0;
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PatchAll(Type type)
        {
            if (BlockPatchAll)
            {
                PatchAllEntered.TrySetResult(true);
                PatchAllRelease.Task.GetAwaiter().GetResult();
            }

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
                        applications.Add(new PatchApplication(Id, method.Name, shortLabel));
                    }
                }
            }

            if (BlockAfterPatchAllPublication)
            {
                PatchAllPublished.TrySetResult(true);
                PatchAllPostPublishRelease.Task.GetAwaiter().GetResult();
            }
        }

        public void UnpatchSelf()
        {
            lock (gate)
            {
                foreach (var entry in owners.Values)
                    entry.Remove(Id);
                applications.RemoveAll(application => string.Equals(application.Owner, Id, StringComparison.Ordinal));
                UnpatchSelfCalls++;
            }
        }

        public static PatchInfo? GetPatchInfo(MethodBase? method)
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
        public static bool BlockAddComponent { get; set; }
        public static TaskCompletionSource<bool> AddComponentEntered { get; private set; } =
            NewSignal();
        public static TaskCompletionSource<bool> AddComponentRelease { get; private set; } =
            NewSignal();

        protected BasePlugin()
        {
            Config = new ConfigFile();
            Log = new ManualLogSource(GetType().Name);
        }

        public ConfigFile Config { get; }
        public ManualLogSource Log { get; }
        public int AddComponentCalls { get; private set; }

        public static void ResetBoundaries()
        {
            BlockAddComponent = false;
            AddComponentEntered = NewSignal();
            AddComponentRelease = NewSignal();
        }

        public virtual void Load()
        {
        }

        public virtual bool Unload() => false;

        protected T AddComponent<T>() where T : MonoBehaviour, new()
        {
            AddComponentCalls++;
            if (BlockAddComponent)
            {
                AddComponentEntered.TrySetResult(true);
                AddComponentRelease.Task.GetAwaiter().GetResult();
            }
            return UnityEngine.Object.AddComponent<T>();
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        internal bool IsDestroyed => destroyed;
        public int InstanceId { get; }
        public IntPtr Pointer => new(InstanceId);

        public int GetInstanceID() => InstanceId;

        public static T AddComponent<T>() where T : MonoBehaviour, new() => new();

        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sortMode)
            where T : Object =>
            objects
                .OfType<T>()
                .Where(item => !item.destroyed
                    && (inactive == FindObjectsInactive.Include
                        || item is not MonoBehaviour behaviour
                        || behaviour.gameObjectActive))
                .ToArray();

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
        public bool gameObjectActive { get; set; } = true;
        public bool isActiveAndEnabled => !IsDestroyed && enabled && gameObjectActive;
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
        public static bool BlockAddQuitting { get; set; }
        public static TaskCompletionSource<bool> AddQuittingEntered { get; private set; } =
            NewSignal();
        public static TaskCompletionSource<bool> AddQuittingRelease { get; private set; } =
            NewSignal();
        public static int AddCalls { get; private set; }
        public static int RemoveCalls { get; private set; }
        public static int CallbackCount
        {
            get { lock (gate) return handlers.Count; }
        }

        public static void add_quitting(Il2CppSystem.Action handler)
        {
            if (BlockAddQuitting)
            {
                AddQuittingEntered.TrySetResult(true);
                AddQuittingRelease.Task.GetAwaiter().GetResult();
            }

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
                BlockAddQuitting = false;
                AddQuittingEntered = NewSignal();
                AddQuittingRelease = NewSignal();
                AddCalls = 0;
                RemoveCalls = 0;
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    /// <summary>
    /// Stand-in for the IL2CPP array wrapper. The scene seam iterates the frame documents and
    /// reads their count, so only enumeration and length are modelled.
    /// </summary>
    public sealed class Il2CppReferenceArray<T> : IEnumerable<T>
    {
        private readonly T[] items;

        public Il2CppReferenceArray(int length) => items = new T[length];

        public Il2CppReferenceArray(params T[] items) => this.items = items;

        public int Length => items.Length;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
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

public sealed class TextMeshProRuby : UnityEngine.MonoBehaviour
{
    private static readonly System.Text.RegularExpressions.Regex RubyMarkup =
        new(@"<r=(?:""([^""]*)""|([^>]*))>(.*?)</r>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string currentText = string.Empty;

    public TMPro.TMP_Text? tmpText { get; set; }
    public bool fixedLineHeight { get; set; } = true;

    public string Text
    {
        get => currentText;
        set
        {
            currentText = value ?? string.Empty;
            Apply();
        }
    }

    public void Apply()
    {
        if (tmpText == null)
            return;

        var expanded = RubyMarkup.Replace(
            currentText,
            static match =>
            {
                var ruby = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var body = match.Groups[3].Value;
                return $"{body}<space=-1.000em><voffset=1em><size=50%>{ruby}</size></voffset>";
            });
        tmpText.text = fixedLineHeight ? $"<line-height=2.000em>{expanded}" : expanded;
    }
}

namespace UniRx
{
    public sealed class ReactiveProperty<T>
    {
        private readonly List<Action<T?>> observers = new();
        private T? currentValue;

        public T? Value
        {
            get => currentValue;
            set
            {
                currentValue = value;
                foreach (var observer in observers.ToArray())
                    observer(value);
            }
        }

        public void Subscribe(Action<T?> observer) => observers.Add(observer);
    }
}

namespace Il2CppSystem.Collections.Generic
{
    public sealed class List<T>
    {
        private readonly System.Collections.Generic.List<T> items = new();

        public int Count => items.Count;
        public T this[int index] => items[index];
        public void Add(T item) => items.Add(item);
        public void Clear() => items.Clear();
    }
}

namespace Assets.GameUi.Service
{
    /// <summary>Only the scene-download seam referenced by production code is modelled.</summary>
    public sealed class EpisodeService
    {
    }
}

namespace Assets.GameUi.Services
{
    public class IScenarioService
    {
        public virtual string ReplaceUsernamePlaceholder(string text) => text;
    }
}

namespace Assets.Api.Client
{
    /// <summary>
    /// Minimal frame document stand-in. The scene seam only reads and writes
    /// <see cref="ConfigurationJson"/>, so no other member is modelled.
    /// </summary>
    public sealed class SceneFrameMaster
    {
        private string configurationJson = string.Empty;

        public long Id { get; set; }
        public int Order { get; set; }
        public Action? OnReadConfigurationJson { get; set; }
        public Action? OnWriteConfigurationJson { get; set; }

        public string ConfigurationJson
        {
            get
            {
                OnReadConfigurationJson?.Invoke();
                return configurationJson;
            }
            set
            {
                OnWriteConfigurationJson?.Invoke();
                configurationJson = value ?? string.Empty;
            }
        }
    }
}

namespace Assets.GameUi.Scenario.Configuration
{
    public enum ScenarioTextSize
    {
        Medium = 0,
    }

    public sealed class ScenarioPhraseConfiguration
    {
        public string Text { get; set; } = string.Empty;
        public string SpeakerName { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public string DisplaySpeakerName { get; set; } = string.Empty;
        public string DisplayTeamName { get; set; } = string.Empty;

        public void ConvertStringToDisplay(Assets.GameUi.Services.IScenarioService scenarioService)
        {
            DisplayText = scenarioService.ReplaceUsernamePlaceholder(Text);
            DisplaySpeakerName = scenarioService.ReplaceUsernamePlaceholder(SpeakerName);
            DisplayTeamName = scenarioService.ReplaceUsernamePlaceholder(TeamName);
        }
    }
}

namespace Assets.GameUi.Scenario.Text
{
    public sealed class ScenarioTextComponent : UnityEngine.MonoBehaviour
    {
        public sealed class SentenceViewModel
        {
            public string Text { get; set; } = string.Empty;
            public Assets.GameUi.Scenario.Configuration.ScenarioTextSize FontSize { get; set; }
            public bool ContainsRuby { get; set; }
            public bool AtEnd { get; set; }
        }

        public global::TextMeshProRuby? sentenceText { get; set; }
        public int ApplySentenceCalls { get; private set; }
        public SentenceViewModel? LastSentence { get; private set; }

        public void ApplySentence(SentenceViewModel sentence)
        {
            ApplySentenceCalls++;
            LastSentence = sentence;
            if (sentenceText != null)
                sentenceText.Text = sentence.Text;
        }
    }

    public sealed class ScenarioTextTimer : UnityEngine.MonoBehaviour
    {
        private static readonly System.Text.RegularExpressions.Regex RubySegment =
            new(@"<r=(?:""[^""]*""|[^>]*)>(.*?)</r>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public sealed class ScenarioTextTimerText
        {
            public ScenarioTextComponent.SentenceViewModel? Sentence { get; set; }
        }

        private string activeRawText = string.Empty;
        private int visibleLength;
        private int totalVisibleLength;

        public UniRx.ReactiveProperty<ScenarioTextTimerText> Model { get; } = new();
        public bool Disposed { get; private set; } = true;
        public int ApplyCalls { get; private set; }
        public Assets.GameUi.Scenario.ScenarioController.TextViewModel? LastModel { get; private set; }

        public void Apply(Assets.GameUi.Scenario.ScenarioController.TextViewModel? model)
        {
            MuvluvLLMMod.Patch.ObserveScenarioTimerApply(this, model);
            ApplyCalls++;
            LastModel = model;
            if (model?.Sentence == null)
            {
                Disposed = true;
                activeRawText = string.Empty;
                visibleLength = 0;
                totalVisibleLength = 0;
                Model.Value = new ScenarioTextTimerText
                {
                    Sentence = new ScenarioTextComponent.SentenceViewModel { Text = string.Empty, AtEnd = true }
                };
                return;
            }

            activeRawText = model.Sentence.Text ?? string.Empty;
            totalVisibleLength = CountVisibleCharacters(activeRawText);
            visibleLength = 0;
            if (totalVisibleLength == 0)
            {
                Disposed = true;
                Model.Value = new ScenarioTextTimerText
                {
                    Sentence = new ScenarioTextComponent.SentenceViewModel { Text = string.Empty, AtEnd = true }
                };
                return;
            }

            Disposed = false;
        }

        public bool StepTick(int characters = 1)
        {
            if (Disposed)
                return false;

            visibleLength = Math.Min(totalVisibleLength, visibleLength + Math.Max(1, characters));
            var slice = SliceTaggedPrefix(activeRawText, visibleLength);
            var atEnd = visibleLength >= totalVisibleLength;
            if (atEnd)
                Disposed = true;

            Model.Value = new ScenarioTextTimerText
            {
                Sentence = new ScenarioTextComponent.SentenceViewModel
                {
                    Text = slice,
                    ContainsRuby = slice.Contains("<r=", StringComparison.OrdinalIgnoreCase),
                    AtEnd = atEnd,
                }
            };
            return !atEnd;
        }

        public void Complete()
        {
            if (!Disposed)
                StepTick(Math.Max(1, totalVisibleLength - visibleLength));
        }

        public void Dispose()
        {
            Disposed = true;
            activeRawText = string.Empty;
            visibleLength = 0;
            totalVisibleLength = 0;
        }

        private static int CountVisibleCharacters(string text) =>
            RubySegment.Replace(text, "$1").Length;

        private static string SliceTaggedPrefix(string text, int maxVisible)
        {
            if (maxVisible <= 0 || string.IsNullOrEmpty(text))
                return string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            var remaining = maxVisible;
            var cursor = 0;
            foreach (System.Text.RegularExpressions.Match match in RubySegment.Matches(text))
            {
                if (remaining <= 0)
                    break;

                if (match.Index > cursor)
                {
                    var plainChunk = text.Substring(cursor, match.Index - cursor);
                    var takePlain = Math.Min(remaining, plainChunk.Length);
                    builder.Append(plainChunk, 0, takePlain);
                    remaining -= takePlain;
                    if (remaining <= 0)
                        return builder.ToString();
                }

                var openEnd = text.IndexOf('>', match.Index);
                var openTag = text.Substring(match.Index, openEnd - match.Index + 1);
                var body = match.Groups[1].Value;
                var takeBody = Math.Min(remaining, body.Length);
                if (takeBody > 0)
                {
                    builder.Append(openTag);
                    builder.Append(body, 0, takeBody);
                    builder.Append("</r>");
                    remaining -= takeBody;
                }

                cursor = match.Index + match.Length;
            }

            if (remaining > 0 && cursor < text.Length)
            {
                var tail = text.Substring(cursor);
                builder.Append(tail, 0, Math.Min(remaining, tail.Length));
            }

            return builder.ToString();
        }
    }
}

namespace Assets.GameUi.Scenario
{
    public sealed class ScenarioFrameProcessor
    {
        public sealed class FrameIdentifierViewModel
        {
            public long Current { get; set; }
        }

        public UniRx.ReactiveProperty<FrameIdentifierViewModel> FrameIdentifier { get; } = new();
    }

    public sealed class ScenarioComponent : UnityEngine.MonoBehaviour
    {
        private Assets.GameUi.Scenario.Text.ScenarioTextTimer? boundTimer;

        public ScenarioController? Controller { get; set; }
        public Assets.GameUi.Scenario.Text.ScenarioTextComponent? textView { get; set; }

        public Assets.GameUi.Scenario.Text.ScenarioTextTimer? textTimer
        {
            get => boundTimer;
            set
            {
                boundTimer = value;
                value?.Model.Subscribe(OnTimerModelChanged);
            }
        }

        public void ClearTextView()
        {
            if (textView?.sentenceText != null)
                textView.sentenceText.Text = string.Empty;
        }

        public void HandoffFrameText(ScenarioController.ScenarioFrameViewModel frame, bool startEmptySentence = true)
        {
            if (frame.Text == null)
            {
                ClearTextView();
                boundTimer?.Apply(null);
                return;
            }

            if (startEmptySentence)
                ClearTextView();

            boundTimer?.Apply(frame.Text);
        }

        private void OnTimerModelChanged(Assets.GameUi.Scenario.Text.ScenarioTextTimer.ScenarioTextTimerText? timerText)
        {
            if (timerText?.Sentence == null)
                return;

            textView?.ApplySentence(timerText.Sentence);
            if (timerText.Sentence.AtEnd)
                Controller?.CompleteTexting(Controller.FrameIdentifier.Value?.Current);
        }
    }

    /// <summary>
    /// Models the scenario controller seams and frame/history view models exercised by the scene
    /// translation and live scenario sync paths.
    /// </summary>
    public sealed class ScenarioController
    {
        private static long nextPointer;
        [Flags]
        public enum FunctionFlags
        {
            None = 0,
        }

        public sealed class TextViewModel
        {
            private static long nextTextModelPointer;

            public IntPtr Pointer { get; } = new(Interlocked.Increment(ref nextTextModelPointer));
            public string Name { get; set; } = string.Empty;
            public string Affiliation { get; set; } = string.Empty;
            public Assets.GameUi.Scenario.Text.ScenarioTextComponent.SentenceViewModel? Sentence { get; set; }
        }

        public sealed class HistoryViewModel
        {
            public long FrameId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Affiliation { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
        }

        public sealed class ScenarioFrameViewModel
        {
            public long Id { get; set; }
            public Assets.Api.Client.SceneFrameMaster? FrameMaster { get; set; }
            public Assets.GameUi.Scenario.Configuration.ScenarioPhraseConfiguration? PhraseConfiguration { get; set; }
            public TextViewModel? Text { get; set; }
        }

        public IntPtr Pointer { get; } = new(Interlocked.Increment(ref nextPointer));
        public long sceneMasterId;
        public int CompletedTextCount { get; private set; }
        public long? LastCompletedFrameId { get; private set; }
        public ScenarioFrameProcessor frameProcessor { get; set; } = new();
        public Il2CppSystem.Collections.Generic.List<HistoryViewModel> histories { get; set; } = new();
        public Assets.GameUi.Services.IScenarioService? scenarioService { get; set; }
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScenarioFrameViewModel>? frames { get; set; }
        public UniRx.ReactiveProperty<ScenarioFrameProcessor.FrameIdentifierViewModel> FrameIdentifier =>
            frameProcessor.FrameIdentifier;

        internal void CompleteTexting(long? frameId)
        {
            CompletedTextCount++;
            LastCompletedFrameId = frameId;
        }

        public void Refresh()
        {
            MuvluvLLMMod.Patch.SetIsPlayingScenario(this);
            histories.Clear();
        }

        // The game declares three Refresh overloads; the seam is the parameterless one.
        public bool Refresh(long sceneMasterId, FunctionFlags disableFunctionFlags, bool a, bool b, bool c) => false;

        public void Refresh(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster> scenes,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster> episodes,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster> frames) { }

        public void Leave() => MuvluvLLMMod.Patch.SetIsNotPlayingScenario(this);

        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScenarioFrameViewModel> GenerateFrames(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Assets.Api.Client.SceneFrameMaster> masters,
            FunctionFlags disableFunctionFlags,
            bool enableDummyBranch = false)
        {
            MuvluvLLMMod.Patch.TranslateSceneFrames(this, masters);
            var generated = new List<ScenarioFrameViewModel>(masters.Length);
            var index = 0;
            foreach (var master in masters)
            {
                index++;
                var frameId = master.Id != 0 ? master.Id : index;
                var phraseText = ExtractPhraseProperty(master.ConfigurationJson, "Text") ?? string.Empty;
                var speakerName = ExtractPhraseProperty(master.ConfigurationJson, "SpeakerName") ?? string.Empty;
                var phrase = new Assets.GameUi.Scenario.Configuration.ScenarioPhraseConfiguration
                {
                    Text = phraseText,
                    SpeakerName = speakerName,
                    DisplayText = scenarioService != null ? scenarioService.ReplaceUsernamePlaceholder(phraseText) : phraseText,
                    DisplaySpeakerName = scenarioService != null ? scenarioService.ReplaceUsernamePlaceholder(speakerName) : speakerName,
                };
                generated.Add(new ScenarioFrameViewModel
                {
                    Id = frameId,
                    FrameMaster = master,
                    PhraseConfiguration = phrase,
                });
            }

            var ordered = generated
                .OrderBy(item => item.FrameMaster?.Order ?? 0)
                .ToArray();
            return new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScenarioFrameViewModel>(ordered);
        }

        public ScenarioFrameViewModel GenerateFrame(ScenarioFrameProcessor.FrameIdentifierViewModel identifier)
        {
            frameProcessor.FrameIdentifier.Value = identifier;
            var frame = frames!.First(item => item.Id == identifier.Current);
            var phrase = frame.PhraseConfiguration!;
            frame.Text = new TextViewModel
            {
                Name = phrase.DisplaySpeakerName,
                Affiliation = phrase.DisplayTeamName,
                Sentence = new Assets.GameUi.Scenario.Text.ScenarioTextComponent.SentenceViewModel
                {
                    Text = phrase.DisplayText,
                    ContainsRuby = phrase.DisplayText.Contains("<r=", StringComparison.OrdinalIgnoreCase),
                    AtEnd = false,
                },
            };
            if (!string.IsNullOrEmpty(phrase.Text))
            {
                histories.Add(new HistoryViewModel
                {
                    FrameId = frame.Id,
                    Name = phrase.DisplaySpeakerName,
                    Affiliation = phrase.DisplayTeamName,
                    Text = System.Text.RegularExpressions.Regex.Replace(
                        phrase.DisplayText,
                        "<r=.*?>(.*?)</r>",
                        "$1",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                });
            }
            return frame;
        }

        private static string? ExtractPhraseProperty(string? json, string propertyName)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                var phrase = root?["Phrase"] as System.Text.Json.Nodes.JsonObject;
                return phrase?[propertyName]?.GetValue<string>();
            }
            catch
            {
                return null;
            }
        }
    }
}
