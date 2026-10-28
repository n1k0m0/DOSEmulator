// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

/// <summary>Actual compiled SharpPixels key callbacks with real IsRepeat events, uninitialized managed receivers and no native window.</summary>
internal static class HostNativeRepeatTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Lazy<UiTypes> ActualUi = new(() => new UiTypes());
    /// <summary>Rejects a real callback/held-state/timing discrepancy without relaxing input admission.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>Loads only the actual sibling GUI assemblies, blocking their P/Invoke and resolving a named desktop dependency when needed.</summary>
    private sealed class ManagedUiContext(string directory) : AssemblyLoadContext("DOSE controlled native-repeat metadata", false)
    {
        private readonly object _guardGate = new();
        private readonly HashSet<Assembly> _guarded = [];
        /// <summary>Actual attempted native library resolutions; every attempt throws before loading a library.</summary>
        public int NativeAttempts { get; private set; }
        /// <summary>Resolves a managed dependency from the selected immutable GUI directory, never creating a window or native handle.</summary>
        protected override Assembly? Load(AssemblyName name)
        {
            string candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate)) return LoadGuarded(candidate);
            if (name.Name is "System.Windows.Forms" or "System.Windows.Forms.Primitives" or "System.Drawing.Common" or "Microsoft.Win32.SystemEvents" or "Accessibility")
            {
                string runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
                string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", "Microsoft.WindowsDesktop.App"));
                string version = Path.GetFileName(runtime);
                string exact = Path.Combine(root, version, name.Name + ".dll");
                if (File.Exists(exact)) return LoadGuarded(exact);
                if (Directory.Exists(root))
                {
                    foreach (string folder in Directory.EnumerateDirectories(root).OrderByDescending(value => value, StringComparer.Ordinal))
                    {
                        string path = Path.Combine(folder, name.Name + ".dll");
                        if (File.Exists(path)) return LoadGuarded(path);
                    }
                }
            }
            return null;
        }
        /// <summary>Installs a native-call blocker immediately after loading the named managed assembly.</summary>
        public Assembly LoadGuarded(string path)
        {
            var assembly = LoadFromAssemblyPath(Path.GetFullPath(path));
            lock (_guardGate)
            {
                if (_guarded.Add(assembly))
                    NativeLibrary.SetDllImportResolver(assembly, (_, _, _) =>
                    {
                        NativeAttempts++;
                        throw new InvalidOperationException("Native access is forbidden in the managed key-callback reproduction.");
                    });
            }
            return assembly;
        }
    }

    /// <summary>Metadata-only bindings to the actual product handler/clock and a concrete no-constructor callback-counting receiver.</summary>
    private sealed class UiTypes
    {
        /// <summary>Managed-only dependency loader retained for the duration of this test process.</summary>
        public ManagedUiContext Context { get; }
        /// <summary>Actual compiled SharpPixels abstract base type.</summary>
        public Type Window { get; }
        /// <summary>Concrete managed callback receiver; its constructor must never be called.</summary>
        public Type Receiver { get; }
        /// <summary>Actual closed held-key HashSet type from the product assembly.</summary>
        public Type HeldType { get; }
        /// <summary>Actual product compatibility key type, distinct from the native framework key enum.</summary>
        public Type KeyType { get; }
        /// <summary>Actual OpenTK keyboard event structure carrying IsRepeat.</summary>
        public Type EventType { get; }
        /// <summary>Actual keyboard event constructor with its explicit native-repeat boolean.</summary>
        public ConstructorInfo EventConstructor { get; }
        /// <summary>Actual native key enum passed to the framework event structure.</summary>
        public Type NativeKey { get; }
        /// <summary>Actual native modifier enum passed to the framework event structure.</summary>
        public Type NativeModifiers { get; }
        /// <summary>Actual private native key-down callback, invoked instead of a copied policy.</summary>
        public MethodInfo Down { get; }
        /// <summary>Actual private native key-up callback.</summary>
        public MethodInfo Up { get; }
        /// <summary>Actual protected context-reset method used by generation/focus/menu transitions.</summary>
        public MethodInfo Reset { get; }
        /// <summary>Actual internal product monotonic repeat-clock type.</summary>
        public Type ClockType { get; }
        /// <summary>Actual independent initial-delay/follow-rate admission method.</summary>
        public MethodInfo TryTake { get; }
        /// <summary>Actual callback-completion admission anchor.</summary>
        public MethodInfo Complete { get; }
        /// <summary>Loads managed metadata and emits only empty/counter overrides; no native settings or constructors run.</summary>
        public UiTypes()
        {
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "net10.0-windows"));
            string path = Path.Combine(directory, "SharpPixels.dll");
            Check(File.Exists(path), "Actual sibling SharpPixels.dll is required for the managed callback reproduction.");
            Context = new(directory);
            var assembly = Context.LoadGuarded(path);
            Window = assembly.GetType("SharpPixels.SharpPixelsWindow", true)!;
            // Allocating an uninitialized object must not activate an unknown OpenTK base initializer.
            for (Type? parent = Window.BaseType; parent is not null; parent = parent.BaseType)
                if (parent.FullName!.StartsWith("OpenTK.Windowing.", StringComparison.Ordinal)) CheckManagedBaseInitializer(parent);
            Down = Window.GetMethod("SharpPixels_KeyDown", InstanceMembers) ?? throw new InvalidDataException("Actual product key-down callback is missing.");
            Up = Window.GetMethod("SharpPixels_KeyUp", InstanceMembers) ?? throw new InvalidDataException("Actual product key-up callback is missing.");
            Reset = Window.GetMethod("ResetKeyRepeat", InstanceMembers) ?? throw new InvalidDataException("Actual product context-reset method is missing.");
            EventType = Down.GetParameters().Single().ParameterType;
            EventConstructor = EventType.GetConstructors().Single(c => c.GetParameters().Length == 4);
            var parameters = EventConstructor.GetParameters();
            NativeKey = parameters[0].ParameterType; NativeModifiers = parameters[2].ParameterType;
            Check(parameters[1].ParameterType == typeof(int) && parameters[3].ParameterType == typeof(bool) &&
                EventType.GetProperty("IsRepeat")?.PropertyType == typeof(bool), "Actual OpenTK native-repeat event contract differs.");
            HeldType = Window.GetField("keysPressed", InstanceMembers)!.FieldType;
            KeyType = HeldType.GetGenericArguments().Single();
            ClockType = assembly.GetType("SharpPixels.KeyRepeatClock", true)!;
            TryTake = ClockType.GetMethod("TryTake", [typeof(long), typeof(uint), typeof(uint), typeof(bool)]) ?? throw new InvalidDataException("Actual product repeat admission API is missing.");
            Complete = ClockType.GetMethod("CompleteRepeat", [typeof(long)]) ?? throw new InvalidDataException("Actual product callback completion API is missing.");
            Receiver = BuildReceiver(Window);
            Check(Context.NativeAttempts == 0, "Managed metadata loading attempted a native call.");
        }
        /// <summary>Permits only the metadata-proven native-free NativeWindow initializer: two parameterless BCL exception collections.</summary>
        private static void CheckManagedBaseInitializer(Type parent)
        {
            var initializer = parent.TypeInitializer; if (initializer is null) return;
            byte[] code = initializer.GetMethodBody()?.GetILAsByteArray()
                ?? throw new InvalidDataException("Unknown OpenTK static initializer has no inspectable managed IL.");
            Check(parent.FullName == "OpenTK.Windowing.Desktop.NativeWindow" && code.Length == 21 &&
                code[0] == 0x73 && code[5] == 0x80 && code[10] == 0x73 && code[15] == 0x80 && code[20] == 0x2a,
                "Refusing unknown OpenTK static initialization; no native window was constructed.");
            foreach (var pair in new (int Offset, string Field, Type Definition)[]
            {
                (0, "_callbackExceptions", typeof(System.Collections.Concurrent.ConcurrentQueue<>)),
                (10, "_localThreadExceptions", typeof(List<>))
            })
            {
                var constructor = initializer.Module.ResolveMethod(BitConverter.ToInt32(code, pair.Offset + 1)) as ConstructorInfo
                    ?? throw new InvalidDataException("OpenTK initializer operand is not a managed constructor.");
                var field = initializer.Module.ResolveField(BitConverter.ToInt32(code, pair.Offset + 6))
                    ?? throw new InvalidDataException("OpenTK initializer field operand is missing.");
                Type collection = constructor.DeclaringType ?? throw new InvalidDataException("OpenTK collection constructor has no declaring type.");
                Check(constructor.GetParameters().Length == 0 && collection.IsGenericType && collection.GetGenericTypeDefinition() == pair.Definition &&
                    collection.GenericTypeArguments.Length == 1 && collection.GenericTypeArguments[0] == typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo) &&
                    field.DeclaringType == parent && field.Name == pair.Field && field.IsStatic && field.FieldType == collection,
                    "Refusing non-collection or native-capable OpenTK initializer operands.");
            }
        }
        /// <summary>Emits actual virtual callback implementations without any base-constructor, graphics or native calls.</summary>
        private static Type BuildReceiver(Type parent)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DOSE.NoNative.KeyReceiver"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("ManagedCallbacks");
            var type = module.DefineType("DOSE.NoNative.KeyReceiver", TypeAttributes.Public | TypeAttributes.Sealed, parent);
            var down = type.DefineField("DownCallbacks", typeof(int), FieldAttributes.Public);
            var up = type.DefineField("UpCallbacks", typeof(int), FieldAttributes.Public);
            var press = type.DefineField("PressCallbacks", typeof(int), FieldAttributes.Public);
            // This valid throwing constructor deliberately has no normal return and never calls any base constructor.
            var ctor = type.DefineConstructor(MethodAttributes.Private, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
            ctor.Emit(OpCodes.Ldstr, "A native-window constructor must never run in this test.");
            ctor.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
            ctor.Emit(OpCodes.Throw);
            foreach (var method in parent.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.IsAbstract))
            {
                Check(method.ReturnType == typeof(void), "Unexpected nonvoid abstract UI callback.");
                var implementation = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());
                var il = implementation.GetILGenerator();
                FieldBuilder? field = method.Name switch { "OnUserKeyDown" => down, "OnUserKeyUp" => up, "OnUserKeyPress" => press, _ => null };
                if (field is not null)
                {
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, field);
                }
                il.Emit(OpCodes.Ret); type.DefineMethodOverride(implementation, method);
            }
            return type.CreateType()!;
        }
    }

    /// <summary>Only managed callback/held/clock fields of an uninitialized receiver; no disposal or native lifetime exists.</summary>
    private sealed class Rig
    {
        private readonly UiTypes _ui = ActualUi.Value;
        private readonly object _window;
        private readonly object _held;
        private readonly object _clock;
        /// <summary>Actual down callbacks dispatched by the compiled product handler.</summary>
        public int Downs => (int)_ui.Receiver.GetField("DownCallbacks")!.GetValue(_window)!;
        /// <summary>Actual up callbacks dispatched by the compiled product handler.</summary>
        public int Ups => (int)_ui.Receiver.GetField("UpCallbacks")!.GetValue(_window)!;
        /// <summary>Actual repeat callbacks; native repeat-downs may not dispatch this separate host policy callback.</summary>
        public int Presses => (int)_ui.Receiver.GetField("PressCallbacks")!.GetValue(_window)!;
        /// <summary>Actual product-held key count, independent from callback counters.</summary>
        public int Held => (int)_ui.HeldType.GetProperty("Count")!.GetValue(_held)!;
        /// <summary>Allocates without constructors, immediately suppresses inherited finalization, then initializes only managed input fields.</summary>
        public Rig()
        {
            _window = RuntimeHelpers.GetUninitializedObject(_ui.Receiver);
            GC.SuppressFinalize(_window);
            _held = Activator.CreateInstance(_ui.HeldType)!;
            _clock = Activator.CreateInstance(_ui.ClockType, true)!;
            _ui.Window.GetField("keysPressed", InstanceMembers)!.SetValue(_window, _held);
            _ui.Window.GetField("_keyRepeatClock", InstanceMembers)!.SetValue(_window, _clock);
            _ui.Window.GetField("_drawingStopwatch", InstanceMembers)!.SetValue(_window, new Stopwatch());
            _ui.Window.GetProperty("KeyPressInitialDelay")!.SetValue(_window, 500u);
            _ui.Window.GetProperty("KeyPressInterval")!.SetValue(_window, 50u);
            Check(_ui.Context.NativeAttempts == 0, "Uninitialized receiver construction attempted a native call.");
        }
        /// <summary>Creates the genuine framework event, with a verified repeat flag and no window-system input.</summary>
        private object Event(string key, bool repeat)
        {
            object value = _ui.EventConstructor.Invoke([Enum.Parse(_ui.NativeKey, key), 30, Enum.ToObject(_ui.NativeModifiers, 0), repeat]);
            Check((bool)_ui.EventType.GetProperty("IsRepeat")!.GetValue(value)! == repeat, "Framework event lost its actual native-repeat flag.");
            return value;
        }
        /// <summary>Calls exactly the compiled private native-down handler; the virtual callback is merely a managed counter.</summary>
        public void Down(bool repeat, string key = "A") { _ui.Down.Invoke(_window, [Event(key, repeat)]); NoNative(); }
        /// <summary>Calls exactly the compiled private native-up handler.</summary>
        public void Up(string key = "A") { _ui.Up.Invoke(_window, [Event(key, false)]); NoNative(); }
        /// <summary>Performs the same actual product context reset used when the UI acknowledges a new input generation.</summary>
        public void Reset(bool clearHeld) { _ui.Reset.Invoke(_window, [clearHeld]); NoNative(); }
        /// <summary>Calls the actual product clock using explicit logical test timestamps and actual held-state enablement.</summary>
        public bool Due(long time) => (bool)_ui.TryTake.Invoke(_clock, [time, 500u, 50u, Held != 0])!;
        /// <summary>Calls the actual completion anchor; no render callback or native surface is invoked.</summary>
        public void Complete(long time) => _ui.Complete.Invoke(_clock, [time]);
        /// <summary>Requires that only managed input code ran.</summary>
        public void NoNative() => Check(_ui.Context.NativeAttempts == 0, "Forbidden native call attempted by a managed key callback.");
    }

    /// <summary>Registers the real-handler reproduction plus fresh-event, held-set and monotonic-clock controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Host native repeat fresh down/up callbacks use the actual compiled handler without a window", () =>
        {
            var rig = new Rig(); rig.Down(false);
            Check(rig.Downs == 1 && rig.Held == 1 && rig.Ups == 0 && rig.Presses == 0, "Fresh actual down did not enter the held set exactly once.");
            rig.Down(true); rig.Down(true);
            Check(rig.Downs == 1 && rig.Held == 1 && rig.Presses == 0, "An already held native repeat dispatched another initial make.");
            rig.Up(); rig.Up();
            Check(rig.Ups == 1 && rig.Held == 0 && rig.Downs == 1, "Actual key-up did not retire exactly one held key.");
            rig.NoNative();
        });
        test("Host native repeat retired held key cannot reenter after actual generation-context clear", () =>
        {
            var rig = new Rig(); rig.Down(false); rig.Reset(true);
            Check(rig.Downs == 1 && rig.Held == 0 && rig.Ups == 0, "Actual context reset fabricated a guest release or retained a hold.");
            rig.Down(true);
            Check(rig.Downs == 1 && rig.Held == 0 && rig.Presses == 0,
                $"Retired native IsRepeat=true reentered as a fresh make: downCallbacks={rig.Downs}, held={rig.Held}.");
            Check(!rig.Due(100000), "Retired native repeat rearmed the clock without a fresh physical down.");
            rig.NoNative();
        });
        test("Host native repeat unowned repeat event alone cannot invent a fresh physical down", () =>
        {
            var rig = new Rig(); rig.Down(true);
            Check(rig.Downs == 0 && rig.Held == 0 && !rig.Due(500), "An IsRepeat=true event was mistaken for the first physical down.");
            rig.NoNative();
        });
        test("Host native repeat genuine fresh down remains accepted after retired key-up", () =>
        {
            var rig = new Rig(); rig.Down(false); rig.Reset(true); rig.Up();
            Check(rig.Ups == 0 && rig.Held == 0, "A retired key-up emitted a release into the new input context.");
            rig.Down(false);
            Check(rig.Downs == 2 && rig.Held == 1, "A genuine new down was lost after retired state was cleared.");
            rig.Up();
            Check(rig.Ups == 1 && rig.Held == 0, "Fresh accepted down/up did not balance.");
            rig.NoNative();
        });
        test("Host native repeat compiled handler retains initial500 follow50 and discards callback stall debt", () =>
        {
            var rig = new Rig(); rig.Down(false); rig.Down(true);
            Check(!rig.Due(499) && rig.Due(500) && !rig.Due(500), "Actual product clock lost its first500ms admission.");
            rig.Complete(10500);
            Check(!rig.Due(10549) && rig.Due(10550) && !rig.Due(10550), "Actual product clock caught up a modal stall or restarted its initial phase.");
            rig.NoNative();
        });
        test("Host native repeat context clear disarms actual clock without a release callback", () =>
        {
            var rig = new Rig(); rig.Down(false); Check(rig.Due(500), "Initial repeat was not armed.");
            rig.Reset(true); rig.Complete(5000);
            Check(rig.Held == 0 && rig.Ups == 0 && !rig.Due(100000), "Context clear revived an old repeat or sent a new-context release.");
            rig.NoNative();
        });
    }
}
