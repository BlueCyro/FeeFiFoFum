using HarmonyLib;
using ResoniteModLoader;
using System.Reflection;
using FrooxEngine;
using Elements.Assets;
using System.Buffers;
using NWaves.Effects;
using NWaves.Filters.Base;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine.CommonAvatar;
using NWaves.Windows;

namespace FeeFiFoFum;

public class FeeFiFoFum : ResoniteMod
{
    public override string Name => "Fee Fi Fo Fum";
    public override string Author => "Cyro";
    public override string Version => "1.0.0";
    public override string Link => "resonite.com";
    public static ModConfiguration? Config;
    public static Dictionary<AudioInput, PitchShiftVocoderEffect> shifters = new();

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> Enabled_Config = new("Pitch shifting enabled", "When checked, enables all FeeFiFoFum functionality", () => true);
    public static bool Enabled => Config!.GetValue(Enabled_Config);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<float> MinPitchFactor_Config = new("Minimum pitch factor", "Minimum percentage the pitch shifter will shift your voice (deepness)", () => 0.85f);
    public static float MinPitchFactor => Config!.GetValue(MinPitchFactor_Config);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<float> MaxPitchFactor_Config = new("Maximum pitch factor", "Maximum percentage the pitch shifter will shift your voice (highness)", () => 1.25f);
    public static float MaxPitchFactor => Config!.GetValue(MaxPitchFactor_Config);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> STFTSize_Config = new("STFT Size", "Size of the STFT performed on the pitch shift vocoder", () => 4096);
    public static int STFTSize => Config!.GetValue(STFTSize_Config);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> STFTOverlap_Config = new("STFT overlap", "How many samples of overlap there are between frames of STFT", () => 1024);
    public static int STFTOverlap => Config!.GetValue(STFTOverlap_Config);

    public override void OnEngineInit()
    {
        Harmony harmony = new("net.Cyro.FeeFiFoFum");
        Config = GetConfiguration();
        Config?.Save(true);
        harmony.PatchAll();
        STFTSize_Config.OnChanged += o =>
        {
            lock(shifters)
            {
                foreach (var shift in shifters.ToList())
                {
                    
                    shifters[shift.Key] = new(shift.Key.SampleRate, shift.Value.Shift, (int)o!, STFTOverlap);
                }
            }
        };

        STFTOverlap_Config.OnChanged += o =>
        {
            lock(shifters)
            {
                foreach (var shift in shifters.ToList())
                {
                    
                    shifters[shift.Key] = new(shift.Key.SampleRate, shift.Value.Shift, STFTSize, (int)o!);
                }
            }
        };
    }

    [HarmonyPatch(typeof(AudioInput))]
    public static class Patch_UserAudioStream
    {
        static readonly MethodInfo GetAudioSystem = AccessTools.Property(typeof(AudioInput), "AudioSystem").GetGetMethod();
        static readonly MethodInfo GetNoiseSupression = AccessTools.Property(typeof(AudioSystem), "NoiseSupression").GetGetMethod();
        static readonly MethodInfo ProxyImplicitOp = AccessTools.FirstMethod(typeof(LocalModeVariableProxy<bool>), m => m.Name == "op_Implicit" && m.ReturnType == typeof(bool));

        static readonly MethodInfo ShiftMethod = AccessTools.Method(typeof(Patch_UserAudioStream), "Shift");
        static readonly CodeInstruction[] pattern = new CodeInstruction[] // Pattern of opcodes to search for in the target method
        {
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, GetAudioSystem),
            new(OpCodes.Callvirt, GetNoiseSupression),
            new(OpCodes.Call, ProxyImplicitOp)
        };

        static readonly CodeInstruction[] insertion = new CodeInstruction[] // Opcodes to be inserted once it's sussed out where to put them
        {
            new(OpCodes.Ldarg_0),
            new(OpCodes.Ldarga_S, 1),
            new(OpCodes.Call, ShiftMethod)
        };

        
        [HarmonyPatch("SetSampleRate")]
        [HarmonyPrefix]
        public static void SetSampleRate_Postfix(AudioInput __instance, int sampleRate, int ___sampleRate)
        {
            lock(shifters)
            {
                if (!shifters.ContainsKey(__instance))
                    shifters.Add(__instance, new(sampleRate, 1f, 4096, 1024));
                
                if (sampleRate != ___sampleRate)
                    shifters[__instance] = new(sampleRate, 1f, 4096, 1024);
            }
        }

        [HarmonyPatch("ProcessNewSamples")] // This is looking to find where a particular if statement branches to so that a call to Shift()
        [HarmonyTranspiler]                 // can be inserted after it. In this case, just after denoising but *before* normalization
        public static IEnumerable<CodeInstruction> ProcessNewSamples_Patch(IEnumerable<CodeInstruction> instructions)
        {
            // It's gonna get noisy in here, fellas.
            
            
            int insertionPoint = -1;

            CodeInstruction[] codes = instructions.ToArray();

            for (int i = 0; i < codes.Length; i++)
            {
                if (i + pattern.Length < codes.Length) // Make sure our window isn't gonna reach outside of the array
                {
                    ArraySegment<CodeInstruction> window = new(codes, i, pattern.Length);
                    bool equal = window // Check if all of the opcodes in the window are equal to the opcodes in our pattern by opcode & operand
                        .Select((i, idx) => 
                            new { i = i, idx = idx }
                        )
                        .All(i => 
                            i.i.opcode == pattern[i.idx].opcode && 
                            i.i.operand == pattern[i.idx].operand
                        );

                    if (equal && codes[i + pattern.Length].Branches(out var label)) // Reasonably certain this is the if, so find the instruction it branches to
                    {
                        insertionPoint = label.HasValue ? codes.FindIndex(i => i.labels.Contains(label.Value)) : -1; // Store the position of this instruction so
                        Msg($"Pattern matched! Found insertion point at: {insertionPoint}");                         // we can insert some extra opcodes after it.
                        break;
                    }
                }
            }
            if (insertionPoint == -1)
            {
                Msg("Failed to find function insertion point, aborting...");
                return instructions;
            }
            
            var codeList = codes.ToList();
            codeList.InsertRange(insertionPoint + 1, insertion); // Insert the extra opcodes just after the target instruction
            return codeList;
        }

        public static void Shift(AudioInput input, ref Span<StereoSample> samples) // Shifts the audio samples based on user scale
        {
            if (!Enabled)
                return;
            
            var engine = Engine.Current;
            var manager = engine?.WorldManager; // Just in case the engine ever, you know, doesn't exist
            var inputInterface = input.Input;
            
            User? localUser = manager?.FocusedWorld?.LocalUser; // ??????? is there a focused world???? Who knows?????
            

            int primaryAudioIndex = inputInterface.DefaultAudioInputIndex;
            AudioInput current = inputInterface.AudioInputs[primaryAudioIndex];
            lock(shifters)
            {
                if (input == current && shifters.TryGetValue(input, out var shifter))
                {
                    shifter.Shift = MathX.Clamp(1f / localUser?.Root.GlobalScale ?? 1f, MinPitchFactor, MaxPitchFactor); 
                    shifter.Process(ref samples);
                }
            }
        }
    }
}

public static class MoreFilterExtensions
{
    public static void Process(this IOnlineFilter filter, ref Span<StereoSample> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new(filter.Process((float)samples[i]));
        }
    }

    public static void TerribleHack(this PitchShiftVocoderEffect effect, float[] window)
    {
        AccessTools.Field(effect.GetType(), "_window").SetValue(effect, window);
    }
}