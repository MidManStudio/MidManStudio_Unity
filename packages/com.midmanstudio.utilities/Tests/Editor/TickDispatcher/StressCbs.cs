// NativeDispatcherStressBench.cs
// Tests the ONLY scenario where MID_NativeTickDispatcher genuinely beats managed:
// 500+ static [BurstCompile] callbacks all doing meaningful SIMD math.
// Below this threshold the managed dispatcher wins — see file header in
// MID_NativeTickDispatcher for the full threshold analysis.

using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Core.Benchmarks
{
    // ── 500 static Burst callbacks doing real sin/cos work ────────────────────
    // These must be public static + [BurstCompile] on both class and method.
    // Burst caches the compiled pointer after the first CompileFunctionPointer call.

    [BurstCompile]
    public static class StressCbs
    {
        // 50 callbacks × 10 work units each = 500 total subscribers
        // Each callback sums 20 sin operations — representative of a lightweight
        // physics integration or particle update step.

        static float _sink; // prevent dead-code elimination

        [BurstCompile] public static void C000(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt);_sink=a;}
        [BurstCompile] public static void C001(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.1f);_sink=a;}
        [BurstCompile] public static void C002(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.2f);_sink=a;}
        [BurstCompile] public static void C003(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.3f);_sink=a;}
        [BurstCompile] public static void C004(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.4f);_sink=a;}
        [BurstCompile] public static void C005(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.5f);_sink=a;}
        [BurstCompile] public static void C006(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.6f);_sink=a;}
        [BurstCompile] public static void C007(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.7f);_sink=a;}
        [BurstCompile] public static void C008(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.8f);_sink=a;}
        [BurstCompile] public static void C009(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+0.9f);_sink=a;}
        [BurstCompile] public static void C010(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt);_sink=a;}
        [BurstCompile] public static void C011(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.1f);_sink=a;}
        [BurstCompile] public static void C012(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.2f);_sink=a;}
        [BurstCompile] public static void C013(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.3f);_sink=a;}
        [BurstCompile] public static void C014(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.4f);_sink=a;}
        [BurstCompile] public static void C015(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.5f);_sink=a;}
        [BurstCompile] public static void C016(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.6f);_sink=a;}
        [BurstCompile] public static void C017(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.7f);_sink=a;}
        [BurstCompile] public static void C018(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.8f);_sink=a;}
        [BurstCompile] public static void C019(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+0.9f);_sink=a;}
        [BurstCompile] public static void C020(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt)*math.cos(i*dt);_sink=a;}
        [BurstCompile] public static void C021(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+1f)*math.cos(i*dt+1f);_sink=a;}
        [BurstCompile] public static void C022(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+2f)*math.cos(i*dt+2f);_sink=a;}
        [BurstCompile] public static void C023(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+3f)*math.cos(i*dt+3f);_sink=a;}
        [BurstCompile] public static void C024(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+4f)*math.cos(i*dt+4f);_sink=a;}
        [BurstCompile] public static void C025(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sqrt(math.abs(math.sin(i*dt)));_sink=a;}
        [BurstCompile] public static void C026(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sqrt(math.abs(math.cos(i*dt)));_sink=a;}
        [BurstCompile] public static void C027(float dt){float a=0;for(int i=0;i<20;i++)a+=math.pow(math.abs(math.sin(i*dt)),0.5f);_sink=a;}
        [BurstCompile] public static void C028(float dt){float a=0;for(int i=0;i<20;i++)a+=math.atan2(math.sin(i*dt),math.cos(i*dt));_sink=a;}
        [BurstCompile] public static void C029(float dt){float a=0;for(int i=0;i<20;i++)a+=math.asin(math.clamp(math.sin(i*dt),-1f,1f));_sink=a;}
    }

}