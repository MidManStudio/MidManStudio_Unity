// StressCbs.cs
// 50 static Burst-compiled callbacks doing real math work.
// All must be public static + [BurstCompile] on both class and method.

using Unity.Burst;
using Unity.Mathematics;

namespace MidManStudio.Core.Benchmarks
{
    [BurstCompile]
    public static class StressCbs
    {
        // Suppress dead-code elimination
        static float _sink;

        // ── Block A: sin (C000–C009) ──────────────────────────────────────────
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

        // ── Block B: cos (C010–C019) ──────────────────────────────────────────
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

        // ── Block C: sin*cos (C020–C029) ──────────────────────────────────────
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

        // ── Block D: heavier (C030–C039) ─────────────────────────────────────
        [BurstCompile] public static void C030(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt)*math.sqrt(i+1);_sink=a;}
        [BurstCompile] public static void C031(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt)*math.sqrt(i+1);_sink=a;}
        [BurstCompile] public static void C032(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*0.05f+dt)*math.cos(i*0.1f+dt);_sink=a;}
        [BurstCompile] public static void C033(float dt){float a=0;for(int i=0;i<20;i++)a+=math.pow(math.abs(math.cos(i*dt+1f)),1.5f);_sink=a;}
        [BurstCompile] public static void C034(float dt){float a=0;for(int i=0;i<20;i++)a+=math.log(math.abs(math.sin(i*dt))+1f);_sink=a;}
        [BurstCompile] public static void C035(float dt){float a=0;for(int i=0;i<20;i++)a+=math.exp(-math.abs(math.sin(i*dt)));_sink=a;}
        [BurstCompile] public static void C036(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(math.sqrt(i+dt));_sink=a;}
        [BurstCompile] public static void C037(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(math.sqrt(i+dt));_sink=a;}
        [BurstCompile] public static void C038(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt)*math.log(i+2);_sink=a;}
        [BurstCompile] public static void C039(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt)*math.log(i+2);_sink=a;}

        // ── Block E: combined (C040–C049) ─────────────────────────────────────
        [BurstCompile] public static void C040(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt+math.cos(i*0.1f));_sink=a;}
        [BurstCompile] public static void C041(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt+math.sin(i*0.1f));_sink=a;}
        [BurstCompile] public static void C042(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt)*math.exp(-i*0.05f);_sink=a;}
        [BurstCompile] public static void C043(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt)*math.exp(-i*0.05f);_sink=a;}
        [BurstCompile] public static void C044(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*i*dt*0.01f);_sink=a;}
        [BurstCompile] public static void C045(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*i*dt*0.01f);_sink=a;}
        [BurstCompile] public static void C046(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(dt/(i+1));_sink=a;}
        [BurstCompile] public static void C047(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(dt/(i+1));_sink=a;}
        [BurstCompile] public static void C048(float dt){float a=0;for(int i=0;i<20;i++)a+=math.sin(i*dt)*math.sin(i*0.3f);_sink=a;}
        [BurstCompile] public static void C049(float dt){float a=0;for(int i=0;i<20;i++)a+=math.cos(i*dt)*math.cos(i*0.3f);_sink=a;}
    }
}
