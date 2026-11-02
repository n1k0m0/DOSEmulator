// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Whpx;

/// <summary>Compares the allocation-free native-store converter with the independent exact rational x87 reference.</summary>
internal static class NativeFpuConversionChecks
{
    /// <summary>Checks signs, rounding ties and adjacent normal/subnormal/overflow exponents with fixed reproducible significands.</summary>
    public static void Run()
    {
        var method=typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxInstructionEmulator")!.GetMethod("StoreFpuBinary",BindingFlags.Static|BindingFlags.NonPublic)!;
        var convert=method.CreateDelegate<Func<Float80Value,int,ushort,Float80Encoding>>();int cases=0;ulong seed=0x34985abced0931;
        foreach(int width in new[]{32,64})
        {
            int bias=width==32?127:1023,shift=width==32?40:11;
            foreach(int exponent in new[]{0,1,16383-bias,16384-bias,16385-bias,16382,16383,16384,16382+bias,16383+bias,16384+bias,32766,32767})
            for(int sample=0;sample<256;sample++)foreach(bool negative in new[]{false,true})for(int rc=0;rc<4;rc++)
            {
                seed=unchecked(seed*6364136223846793005+1442695040888963407);
                ulong significand=sample switch{0=>0,1=>0x8000000000000000,2=>ulong.MaxValue,3=>0x8000000000000000|(1UL<<(shift-1)),4=>0x8000000000000000|(3UL<<(shift-1)),_=>seed|0x8000000000000000};
                var number=new Float80Value(significand,(ushort)(exponent|(negative?0x8000:0)));ushort control=(ushort)(0x37f|rc<<10);
                if(convert(number,width,control)!=Float80Math.StoreBinary(number,width,control))throw new Exception($"Native raw store conversion differs {width}/{exponent}/{significand:X16}/{rc}/{negative}");
                cases++;
            }
        }
        Console.WriteLine($"PASS: native raw x87 store conversion {cases} exact rational differential cases; normal/tie/underflow/overflow/special boundaries and both signs.");
    }
}
