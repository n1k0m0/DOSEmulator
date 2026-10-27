// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80TrigResult(Float80Result Number, bool OutOfRange = false);

public static partial class Float80Math
{
    /// <summary>FSIN/FCOS: full-precision integer intervals with explicit range and rounding.</summary>
    public static Float80TrigResult SineCosine(Float80Value x, bool cosine, ushort control)
    {
        if (Unsupported(x) || Infinity(x)) return new(Special(Float80Value.Indefinite, Invalid, control));
        if (Nan(x)) return new(Special(x with { Significand=x.Significand|QuietBit }, Signaling(x)?Invalid:(ushort)0, control));
        if (x.Exponent >= Bias+63) return new(new(x, Commit:false), true);
        ushort initial = DenormalOperand(x)?Denormal:(ushort)0;
        if ((initial&~control)!=0) return new(new(x,initial,false,false));
        if (Zero(x)) return new(new(cosine?Float80Value.One:x));
        BigInteger n=x.Significand,d=BigInteger.One;
        int scale=Scale(x);
        if (scale>=0) n<<=scale; else d<<=-scale;
        if ((n<<64)<d)
        {
            // Strict endpoints resolve exact binary boundaries even at 2^-16445.
            // sin(z): z-z^3/6 < sin(z) < z-z^3/6+z^5/120.
            // cos(z): 1-z^2/2 < cos(z) < 1-z^2/2+z^4/24.
            BigInteger nn=n*n,dd=d*d;
            var a=cosine?EncodeLogBound(2*dd-nn,2*dd,0,false,control,initial):
                EncodeLogBound(n*(6*dd-nn),6*d*dd,0,x.Negative,control,initial);
            var b=cosine?EncodeLogBound(24*dd*dd-12*nn*dd+nn*nn,24*dd*dd,0,false,control,initial):
                EncodeLogBound(n*(120*dd*dd-20*nn*dd+nn*nn),120*d*dd*dd,0,x.Negative,control,initial);
            if (a==b) return new(a);
        }
        for (int bits=256;;bits=checked(bits+128))
        {
            BigInteger unit=BigInteger.One<<bits;
            var pi=PiBounds.GetOrAdd(bits,ComputePiBounds);
            BigInteger kl=(4*n*unit+d*pi.High)/(2*d*pi.High),kh=(4*n*unit+d*pi.Low)/(2*d*pi.Low);
            if (kl!=kh) continue;
            BigInteger lo=n*unit/d-CeilingDivide(kl*pi.High,2),hi=CeilingDivide(n*unit,d)-kl*pi.Low/2;
            if (lo<=0&&hi>=0 || BigInteger.Abs(lo)<8 || BigInteger.Abs(hi)<8) continue;
            int quadrant=(int)(kl&3);
            bool useCosine=cosine^((quadrant&1)!=0);
            bool negative=(cosine?quadrant is 1 or 2:quadrant>=2)^(!cosine&&x.Negative);
            BigInteger al=BigInteger.Min(BigInteger.Abs(lo),BigInteger.Abs(hi)),ah=BigInteger.Max(BigInteger.Abs(lo),BigInteger.Abs(hi));
            BigInteger low,high;
            if (useCosine)
            {
                low=TrigSeriesBounds(ah,unit,false).Low;high=TrigSeriesBounds(al,unit,false).High;
            }
            else
            {
                low=al*TrigSeriesBounds(al,unit,true).Low/unit;
                high=CeilingDivide(ah*TrigSeriesBounds(ah,unit,true).High,unit);
                negative^=lo.Sign<0;
            }
            if (low<=0) continue;
            var a=EncodeLogBound(low,unit,0,negative,control,initial);
            var b=EncodeLogBound(high,unit,0,negative,control,initial);
            if (a==b) return new(a);
        }
    }
}
