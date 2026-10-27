// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections.Concurrent;
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    private static readonly ConcurrentDictionary<int,(BigInteger Low,BigInteger High)> PiBounds = new();

    /// <summary>FPATAN: full-range atan2(y,x), with outward integer intervals and one final rounding.</summary>
    public static Float80Result ArcTangent(Float80Value x, Float80Value y, ushort control)
    {
        if (Unsupported(x) || Unsupported(y) || Nan(x) || Nan(y))
            return Calculate(x,y,Float80Operation.Add,control);
        ushort initial = DenormalOperand(x) || DenormalOperand(y) ? Denormal : (ushort)0;
        if ((initial & ~control) != 0) return new(y,initial,false,false);
        bool negative = y.Negative;
        if (!x.Negative && (Zero(y) || Infinity(x) && !Infinity(y))) return new(SignedZero(negative),initial);

        BigInteger n = 0, d = 1;
        bool swapped = false;
        int piNumerator = 0, piDenominator = 1;
        if (Zero(y) || Infinity(x) && !Infinity(y)) piNumerator = 1;
        else if (Zero(x) || Infinity(y) && !Infinity(x)) { piNumerator = 1; piDenominator = 2; }
        else if (Infinity(x) && Infinity(y)) { piNumerator = x.Negative ? 3 : 1; piDenominator = 4; }
        else
        {
            n = y.Significand; d = x.Significand;
            int shift = Scale(y) - Scale(x);
            if (shift >= 0) n <<= shift; else d <<= -shift;
            if (n > d) { (n,d) = (d,n); swapped = true; }
        }
        if (piNumerator==0 && !swapped && !x.Negative && (n<<128)<d)
        {
            // Strict rational bounds below r are necessary at exact binary
            // rounding boundaries. Fixed point 1+/-epsilon around atan(r)/r
            // would otherwise need tens of thousands of guard bits.
            BigInteger nn=n*n,dd=d*d;
            var low=EncodeLogBound(n*(3*dd-nn),3*d*dd,0,negative,control,initial);
            var high=EncodeLogBound(n*(15*dd*dd-5*nn*dd+3*nn*nn),15*d*dd*dd,0,negative,control,initial);
            if (low==high) return low;
        }
        for (int bits = 192; ; bits = checked(bits + 128))
        {
            BigInteger unit = BigInteger.One << bits;
            var pi = PiBounds.GetOrAdd(bits,ComputePiBounds);
            Float80Result a,b;
            if (piNumerator != 0)
            {
                a = EncodeLogBound(pi.Low * piNumerator,unit * piDenominator,0,negative,control,initial);
                b = EncodeLogBound(pi.High * piNumerator,unit * piDenominator,0,negative,control,initial);
            }
            else
            {
                BigInteger lo,hi;
                if (2*n <= d)
                {
                    var ratio = AtanSmallRatioBounds(n,d,unit);
                    if (!swapped && !x.Negative)
                    {
                        // Keep the exact ratio outside fixed point for exponent
                        // differences down to -32828; no artificial zero angle.
                        a = EncodeLogBound(n*ratio.Low,d*unit,0,negative,control,initial);
                        b = EncodeLogBound(n*ratio.High,d*unit,0,negative,control,initial);
                        if (a==b) return a;
                        continue;
                    }
                    lo = n*ratio.Low/d; hi = CeilingDivide(n*ratio.High,d);
                }
                else
                {
                    // atan(r)=pi/4-atan((1-r)/(1+r)), with reduced ratio <=1/3.
                    var ratio = AtanSmallRatioBounds(d-n,d+n,unit);
                    lo = pi.Low/4-CeilingDivide((d-n)*ratio.High,d+n);
                    hi = CeilingDivide(pi.High,4)-(d-n)*ratio.Low/(d+n);
                }
                if (swapped) (lo,hi) = (pi.Low/2-hi,CeilingDivide(pi.High,2)-lo);
                if (x.Negative) (lo,hi) = (pi.Low-hi,pi.High-lo);
                a = EncodeLogBound(lo,unit,0,negative,control,initial);
                b = EncodeLogBound(hi,unit,0,negative,control,initial);
            }
            if (a==b) return a;
        }
    }

    // atan(r)/r = 1-r^2/3+r^4/5-..., 0<=r<=1/2. All recurrence
    // operations round outwards. The omitted alternating tail is <2 units.
    private static (BigInteger Low,BigInteger High) AtanSmallRatioBounds(BigInteger n,BigInteger d,BigInteger unit)
    {
        BigInteger ul=n*n*unit/(d*d), uh=CeilingDivide(n*n*unit,d*d);
        BigInteger tl=unit, th=unit, sl=unit, sh=unit;
        for (int j=1; ; j++)
        {
            tl=tl*ul/unit; th=CeilingDivide(th*uh,unit);
            BigInteger l=tl/(2*j+1),h=CeilingDivide(th,2*j+1);
            if ((j&1)!=0) { sl-=h;sh-=l; } else { sl+=l;sh+=h; }
            if (th<=1) return (sl-2,sh+2);
        }
    }

    /// <summary>Builds reconstructible pi bounds using the Machin identity, independently of host floating point.</summary>
    private static (BigInteger Low,BigInteger High) ComputePiBounds(int bits)
    {
        BigInteger unit=BigInteger.One<<bits;
        var a=AtanSmallRatioBounds(1,5,unit);var b=AtanSmallRatioBounds(1,239,unit);
        // Machin identity; this cache is a reproducible constant, not guest state.
        return (16*(a.Low/5)-4*CeilingDivide(b.High,239),16*CeilingDivide(a.High,5)-4*(b.Low/239));
    }
}
