// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    /// <summary>Original three-byte foreground color stack; no second color store is retained in CR4A.</summary>
    private byte[] _trioCursorForeground = new byte[3];
    /// <summary>Original three-byte background color stack; no second color store is retained in CR4B.</summary>
    private byte[] _trioCursorBackground = new byte[3];
    /// <summary>Shared foreground/background stack position, reset by an actual readable CR45 transaction.</summary>
    private byte _trioCursorColorIndex;
    /// <summary>X origin registered by the last actual CR48 write, separately from pending CRTC programming.</summary>
    private ushort _trioCursorOriginX;
    /// <summary>Y origin registered by the last actual CR48 write, separately from pending CRTC programming.</summary>
    private ushort _trioCursorOriginY;

    /// <summary>Current registered hardware-cursor X coordinate, not a host cursor position.</summary>
    public ushort TrioCursorOriginX => _trioCursorOriginX;
    /// <summary>Current registered hardware-cursor Y coordinate, not a host cursor position.</summary>
    public ushort TrioCursorOriginY => _trioCursorOriginY;
    /// <summary>Current shared three-byte color stack position, including incomplete guest writes.</summary>
    public byte TrioCursorColorIndex => _trioCursorColorIndex;
    /// <summary>Resets only the five cursor states; the board's undefined initial colors use an explicit zero policy.</summary>
    private void ResetTrioCursor()
    {Array.Clear(_trioCursorForeground);Array.Clear(_trioCursorBackground);_trioCursorColorIndex=0;_trioCursorOriginX=_trioCursorOriginY=0;}
    /// <summary>Implements cursor register read effects after normal mono/color decode and the original CRTC read locks.</summary>
    private byte ReadTrioCrtcValue(byte index)
    {
        if(index==0x45)_trioCursorColorIndex=0;
        return index switch{0x4a=>_trioCursorForeground[_trioCursorColorIndex],0x4b=>_trioCursorBackground[_trioCursorColorIndex],_=>_crtc[index]};
    }
    /// <summary>Accepts one real color-stack byte, always advancing the common pointer even for an unchanged color value.</summary>
    private bool WriteTrioCursorStack(byte index,byte value)
    {
        if(index is not (0x4a or 0x4b))return false;
        byte[] colors=index==0x4a?_trioCursorForeground:_trioCursorBackground;
        if(colors[_trioCursorColorIndex]!=value){colors[_trioCursorColorIndex]=value;_pixelRevision++;}
        _trioCursorColorIndex=(byte)((_trioCursorColorIndex+1)%3);return true;
    }
    /// <summary>Registers the original11-bit X/Y coordinate pair even when CR48's own stored byte does not change.</summary>
    private void CommitTrioCursorOrigin()
    {
        ushort x=(ushort)((_crtc[0x46]<<8)|_crtc[0x47]),y=(ushort)((_crtc[0x48]<<8)|_crtc[0x49]);
        if(x!=_trioCursorOriginX||y!=_trioCursorOriginY){_trioCursorOriginX=x;_trioCursorOriginY=y;_pixelRevision++;}
    }
    /// <summary>Converts the cursor stack's low bytes through the selected functional PA/RAMDAC format.</summary>
    private uint TrioCursorColor(byte[] stack,int bits)
    {
        if(bits==8)return _rgb[stack[0]&_pelMask];
        uint value=(uint)(stack[0]|(stack[1]<<8));
        if(bits==24)return 0xff000000u|((uint)stack[2]<<16)|value;
        uint red=(value>>(bits==15?10:11))&31,green=(value>>5)&(bits==15?31u:63u),blue=value&31;
        red=(red<<3)|(red>>2);blue=(blue<<3)|(blue>>2);green=bits==15?(green<<3)|(green>>2):(green<<2)|(green>>4);
        return 0xff000000u|(red<<16)|(green<<8)|blue;
    }
    /// <summary>Inverts the functional pixel-address input for indexed mode; direct-color inversion commutes with component expansion.</summary>
    private uint InvertTrioCursorPixel(int x,int y,uint current,int bits)
    {
        if(bits!=8)return current^0x00ffffffu;
        int address=TrioDisplayStart*4+y*TrioOffset*8+x;
        return _rgb[(~ReadTrioRaw(address))&_pelMask];
    }
    /// <summary>Composes the actual contiguous1-KiB guest VRAM mask with the enhanced scanout, without changing guest memory or timing.</summary>
    private void RenderTrioCursor(int width,int height,uint[] pixels,int colorBits)
    {
        if((_crtc[0x45]&0x11)!=1)return; // The original right-storage arrangement remains separately unimplemented.
        int offsetX=_crtc[0x4e],offsetY=_crtc[0x4f],originX=_trioCursorOriginX,originY=_trioCursorOriginY;
        int lastX=Math.Min(width,originX+64-offsetX),lastY=Math.Min(height,originY+64-offsetY);
        int storage=((_crtc[0x4c]<<8)|_crtc[0x4d])*1024;bool x11=(_crtc[0x55]&0x10)!=0;
        uint foreground=TrioCursorColor(_trioCursorForeground,colorBits),background=TrioCursorColor(_trioCursorBackground,colorBits);
        for(int y=originY;y<lastY;y++)for(int x=originX;x<lastX;x++)
        {
            int sx=x-originX+offsetX,sy=y-originY+offsetY;
            // Two adjacent AND bytes, then two XOR bytes, for each16-pixel word group; each byte is scanned MSB first.
            int address=storage+sy*16+(sx>>4)*4+((sx>>3)&1),bit=0x80>>(sx&7);
            bool and=(ReadTrioRaw(address)&bit)!=0,xor=(ReadTrioRaw(address+2)&bit)!=0;int output=y*width+x;
            if(x11){if(and)pixels[output]=xor?foreground:background;}
            else if(!and)pixels[output]=xor?foreground:background;
            else if(xor)pixels[output]=InvertTrioCursorPixel(x,y,pixels[output],colorBits);
        }
    }
    /// <summary>Reads one mandatory cursor-state field without consuming a runtime state reader or opening media.</summary>
    private static T TrioCursorField<T>(StateBlock state,string name)
    {
        if(!state.Fields.TryGetValue(name,out var value))throw new InvalidDataException("Missing Trio64 cursor state: "+name);
        try{T result=value.Deserialize<T>(StateValues.Json)!;if(result is null)throw new InvalidDataException("Null Trio64 cursor state: "+name);return result;}
        catch(JsonException error){throw new InvalidDataException("Invalid Trio64 cursor state: "+name,error);}
    }
    /// <summary>Checks the five explicit cursor fields and canonical CRTC placeholders before media opening or raw-state adoption.</summary>
    public static void ValidateTrio64CursorState(StateBlock state,bool isTrio64)
    {
        var fg=TrioCursorField<byte[]>(state,"_trioCursorForeground");var bg=TrioCursorField<byte[]>(state,"_trioCursorBackground");
        byte index=TrioCursorField<byte>(state,"_trioCursorColorIndex");ushort x=TrioCursorField<ushort>(state,"_trioCursorOriginX"),y=TrioCursorField<ushort>(state,"_trioCursorOriginY");
        if(fg.Length!=3||bg.Length!=3||index>=3||x>2047||y>2047)throw new InvalidDataException("Invalid Trio64 cursor shape, color index or origin.");
        if(!isTrio64){if(!IsTrio64CursorResetState(state))throw new InvalidDataException("A non-Trio adapter has foreign hardware-cursor state.");return;}
        var crtc=TrioCursorField<byte[]>(state,"_crtc");
        if(crtc.Length!=128||crtc[0x4a]!=0||crtc[0x4b]!=0||(y>>8)!=crtc[0x48])throw new InvalidDataException("Invalid canonical Trio64 cursor register image.");
        for(int register=0x45;register<=0x4f;register++)
            if((crtc[register]&~TrioCrtcMask(register))!=0)throw new InvalidDataException("Invalid Trio64 cursor register bits.");
        // X and the low Y byte may legitimately differ: programming only registers a new pair on the next CR48 write.
    }
    /// <summary>Requires the board's complete zero cursor state for non-Trio profiles and held PCI reset.</summary>
    public static bool IsTrio64CursorResetState(StateBlock state)
    {
        var fg=TrioCursorField<byte[]>(state,"_trioCursorForeground");var bg=TrioCursorField<byte[]>(state,"_trioCursorBackground");
        return fg.Length==3&&bg.Length==3&&fg.All(x=>x==0)&&bg.All(x=>x==0)&&TrioCursorField<byte>(state,"_trioCursorColorIndex")==0&&
            TrioCursorField<ushort>(state,"_trioCursorOriginX")==0&&TrioCursorField<ushort>(state,"_trioCursorOriginY")==0;
    }
}
