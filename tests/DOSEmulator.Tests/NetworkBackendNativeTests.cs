// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Runtime.Networking;


/// <summary>Explicit real IPv4/pipe checks against controlled loopback peers; ordinary regressions never open host sockets.</summary>
internal static class NetworkBackendNativeTests
{
    /// <summary>Checks actual libslirp packet exchanges, isolated shared Ethernet and final worker shutdown without Internet traffic.</summary>
    public static int Run(string nativeDirectory, string output)
    {
        string native=Path.GetFullPath(nativeDirectory);
        var records=new List<object>();
        byte[] mac=[2,0x44,0x4f,0x53,0x45,0x71], guest=[10,0,2,15], gateway=[10,0,2,2];
        byte[] gatewayMac=[0x52,0x55,10,0,2,2];

        using(var disconnected=new SlirpEthernetBackend(false,false,native)) Check(!disconnected.IsConnected,"default disconnected");
        using(var backend=new SlirpEthernetBackend(false,true,native))
        {
         Check(backend.IsConnected,backend.Error??"NAT initialization");
         backend.Send(Arp(mac,new byte[6],new byte[4],gateway,1));
         var arp=Wait(backend,p=>p.Length>=42 && U16(p,12)==0x806 && U16(p,20)==2);
         Check(arp.AsSpan(22,6).SequenceEqual(gatewayMac)&&arp.AsSpan(28,4).SequenceEqual(gateway),"gateway ARP bytes");
         var offer=Dhcp(backend,mac,0x175A0101,1,null);
         Check(offer.Address.SequenceEqual(guest)&&offer.Kind==2,"actual NAT DHCP offer");
         var ack=Dhcp(backend,mac,0x175A0101,3,guest);
         Check(ack.Address.SequenceEqual(guest)&&ack.Kind==5,"actual NAT DHCP acknowledgement");
         UdpRoundTrip(backend,mac,guest,gateway,gatewayMac);
         records.Add(new {mode="NAT",arp=true,arpFrameBytes=arp.Length,dhcpOffer=true,dhcpAck=true,udpBytes=257,endpoint="controlled loopback only"});
         TcpRoundTrip(backend,mac,guest,gateway,gatewayMac);
         records.Add(new {mode="NAT",tcpHandshake=true,tcpBytesEachWay=1027,endpoint="controlled loopback only"});
        }
        using(var backend=new SlirpEthernetBackend(true,true,native))
        {
         Check(backend.IsConnected,backend.Error??"host-only initialization");
         var offer=Dhcp(backend,mac,0x175A0202,1,null); Check(offer.Kind==2,"host-only DHCP");
         Check(Dhcp(backend,mac,0x175A0202,3,offer.Address).Kind==5,"host-only DHCP ACK");
         CheckGateway(backend,mac,offer.Address,gateway,gatewayMac); UdpRoundTrip(backend,mac,offer.Address,gateway,gatewayMac);
         TcpRoundTrip(backend,mac,offer.Address,gateway,gatewayMac);
         Check(!SlirpEthernetBackend.HostOnlyFrameAllowed(Udp(mac,guest,new byte[]{192,0,2,1},27001,9,new byte[8],gatewayMac)),"host-only rejects external IPv4");
         Check(!SlirpEthernetBackend.HostOnlyFrameAllowed(Udp(mac,guest,new byte[]{10,0,2,3},27001,53,new byte[12],gatewayMac)),"host-only rejects external DNS gateway");
         records.Add(new {mode="Host-only direct stack",arp=true,dhcp=true,udpBytesEachWay=257,tcpBytesEachWay=1027,externalIpv4Rejected=true,externalDnsRejected=true,endpoint="controlled loopback via 10.0.2.2 only"});
        }
        string lan="own-check-"+Guid.NewGuid().ToString("N");
        using(var a=new HostOnlyLanBackend(lan,true,native))
        using(var b=new HostOnlyLanBackend(lan,true,native))
        {
         Check(a.IsConnected,a.Error??"LAN A");Check(b.IsConnected,b.Error??"LAN B");
         byte[] frame=Enumerable.Range(0,1514).Select(i=>(byte)(i*37+1)).ToArray();
         frame.AsSpan(0,6).Fill(255);frame[12]=0x81;frame[13]=0x37;
         a.Send(frame);var rx=Wait(b,p=>p.Length==frame.Length&&U16(p,12)==0x8137);
         Check(rx.SequenceEqual(frame),"full independent 1514-byte non-IP Ethernet across shared LAN");
         var one=Dhcp(a,mac,0x175A0303,1,null);
         var two=Dhcp(b,new byte[]{2,0x44,0x4f,0x53,0x45,0x72},0x175A0404,1,null);
         Check(!one.Address.SequenceEqual(two.Address),"one gateway, distinct DHCP leases");
         Check(Dhcp(a,mac,0x175A0303,3,one.Address).Kind==5,"shared Host-only DHCP ACK");
         CheckGateway(a,mac,one.Address,gateway,gatewayMac); UdpRoundTrip(a,mac,one.Address,gateway,gatewayMac); TcpRoundTrip(a,mac,one.Address,gateway,gatewayMac);
         records.Add(new {mode="shared Host-only LAN",ipxEtherTypeBytes=1514,distinctDhcpOffers=true,udpBytesEachWay=257,tcpBytesEachWay=1027,endpoint="controlled loopback via 10.0.2.2 only"});
        }
        using(var again=new HostOnlyLanBackend(lan,true,native)) Check(again.IsConnected,again.Error??"immediate reconnect");
        records.Add(PeerCapacity(native)); records.Add(CrossProcess(native));
        var result = new {passed=true,records,physicalBridgeOpened=false,externalTrafficSent=false};
        string report=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write))
        using(var writer=new StreamWriter(file)) writer.Write(report);
        Console.WriteLine(JsonSerializer.Serialize(new {passed=true,output=Path.GetFullPath(output),records=records.Count}));
        return 0;
    }

    /// <summary>Runs only the explicitly selected same-user child peer; no ordinary regression or Internet endpoint is entered.</summary>
    public static int RunPeer(string nativeDirectory,string lan,string nonceText)
    {
        Check(lan.StartsWith("own-process-",StringComparison.Ordinal)&&Guid.TryParseExact(lan[12..],"N",out _),"own bounded child LAN name");
        Check(Guid.TryParseExact(nonceText,"N",out var nonce),"own child nonce"); string native=Path.GetFullPath(nativeDirectory);
        using var peer=new HostOnlyLanBackend(lan,true,native); Check(peer.IsConnected,peer.Error??"child LAN peer failed");
        Console.WriteLine("READY "+nonceText); Console.Out.Flush();
        Check(ReadProtocol(Console.In,TimeSpan.FromSeconds(15))=="GO "+nonceText,"parent GO handshake");
        byte[] expected=IpxFrame(1514,0x31,nonce,0xa1);
        byte[] request=Wait(peer,p=>IsOwnFrame(p,nonce,0x31,1514)); Check(request.SequenceEqual(expected),"every actual child request byte");
        peer.Send(request);
        byte[] ack=Wait(peer,p=>IsOwnFrame(p,nonce,0x32,60)); Check(ack.SequenceEqual(IpxFrame(60,0x32,nonce,0xa1)),"every actual child ACK byte");
        // A received ACK proves that the echo traversed the child's queued pipe
        // writer and the parent, before either side is allowed to dispose it.
        Console.WriteLine("ACKED "+nonceText); Console.Out.Flush();
        Check(ReadProtocol(Console.In,TimeSpan.FromSeconds(15))=="EXIT "+nonceText,"parent EXIT handshake");
        return 0;
    }

    /// <summary>Verifies all sixteen real LAN ports and rejects a seventeenth without changing an existing live port.</summary>
    private static object PeerCapacity(string native)
    {
        string lan="own-bound-"+Guid.NewGuid().ToString("N"); Guid nonce=Guid.NewGuid(); var peers=new List<HostOnlyLanBackend>();
        try
        {
            for(int i=0;i<16;i++)
            {
                var peer=new HostOnlyLanBackend(lan,true,native); peers.Add(peer); Check(peer.IsConnected,peer.Error??"LAN peer "+i);
            }
            using(var excess=new HostOnlyLanBackend(lan,true,native))
                Check(!excess.IsConnected&&!string.IsNullOrWhiteSpace(excess.Error),"seventeenth LAN port must be rejected");
            byte[] forward=IpxFrame(1514,0x41,nonce,0xa0); peers[0].Send(forward);
            for(int i=1;i<16;i++) Check(Wait(peers[i],p=>IsOwnFrame(p,nonce,0x41,1514)).SequenceEqual(forward),"all 1514 bytes at live bounded peer "+i);
            byte[] reverse=IpxFrame(1514,0x42,nonce,0xaf); peers[15].Send(reverse);
            for(int i=0;i<15;i++) Check(Wait(peers[i],p=>IsOwnFrame(p,nonce,0x42,1514)).SequenceEqual(reverse),"all 1514 reverse bytes at bounded peer "+i);
            Check(peers.All(p=>p.IsConnected),"rejected seventeenth leaves all sixteen original ports connected");
            return new {mode="Host-only peer bound",connectedPeers=16,seventeenthRejected=true,bytesPerFrame=1514,forwardRecipients=15,reverseRecipients=15};
        }
        finally
        {
            List<Exception>? errors=null;
            for(int i=peers.Count-1;i>=0;i--) try {peers[i].Dispose();} catch(Exception error){(errors??=[]).Add(error);}
            if(errors is not null) throw new AggregateException("Bounded LAN peers could not all retire.",errors);
        }
    }

    /// <summary>Exchanges complete raw Ethernet with one own dotnet child and proves both writers flushed before final process join.</summary>
    private static object CrossProcess(string native)
    {
        string lan="own-process-"+Guid.NewGuid().ToString("N"),nonceText=Guid.NewGuid().ToString("N"); Guid nonce=Guid.ParseExact(nonceText,"N");
        using var parent=new HostOnlyLanBackend(lan,true,native); Check(parent.IsConnected,parent.Error??"parent process LAN");
        string assembly=typeof(NetworkBackendNativeTests).Assembly.Location;
        var info=new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            WorkingDirectory=Path.GetDirectoryName(assembly)??throw new InvalidOperationException("Test assembly directory unavailable.")
        };
        info.ArgumentList.Add(assembly);info.ArgumentList.Add("--network-native-peer");info.ArgumentList.Add(native);info.ArgumentList.Add(lan);info.ArgumentList.Add(nonceText);
        using var child=new Process {StartInfo=info}; Check(child.Start(),"own bounded dotnet child did not start");
        Task<string> errors=DrainBounded(child.StandardError,16384); Exception? failure=null; string stderr="";
        try
        {
            Check(ReadProtocol(child.StandardOutput,TimeSpan.FromSeconds(20))=="READY "+nonceText,"child READY handshake");
            WriteProtocol(child.StandardInput,"GO "+nonceText);
            byte[] expected=IpxFrame(1514,0x31,nonce,0xa1); parent.Send(expected);
            byte[] echo=Wait(parent,p=>IsOwnFrame(p,nonce,0x31,1514)); Check(echo.SequenceEqual(expected),"all 1514 IPX EtherType bytes returned from a separate process");
            parent.Send(IpxFrame(60,0x32,nonce,0xa1));
            Check(ReadProtocol(child.StandardOutput,TimeSpan.FromSeconds(10))=="ACKED "+nonceText,"child received real ACK after its echo writer flushed");
            WriteProtocol(child.StandardInput,"EXIT "+nonceText);
            child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            stderr=errors.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); Check(child.ExitCode==0,"child exit "+child.ExitCode+": "+stderr);
        }
        catch(Exception error){failure=error;}
        finally
        {
            try
            {
                if(!child.HasExited)
                {
                    try {WriteProtocol(child.StandardInput,"EXIT "+nonceText);} catch(Exception error) when(error is IOException or InvalidOperationException or TimeoutException) { }
                    if(!child.WaitForExit(3000)) {child.Kill(entireProcessTree:true);Check(child.WaitForExit(5000),"own child did not join after bounded kill");}
                }
                try {child.StandardInput.Close();} catch(IOException) { }
                stderr=errors.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch(Exception cleanup){failure=failure is null?cleanup:new AggregateException(failure,cleanup);}
        }
        if(failure is not null) throw new IOException("Separate-process Host-only LAN check failed. Child diagnostics: "+stderr,failure);
        return new {mode="separate-process Host-only LAN",childProcess=true,requestBytes=1514,echoBytes=1514,ackBytes=60,writerFlushAcknowledged=true,childExit=child.ExitCode,childJoined=true,externalEndpointOpened=false};
    }

    /// <summary>Uses this process's own dotnet runtime host, avoiding a shell, PATH lookup or another test assembly.</summary>
    private static string DotnetHost()
    {
        string? active=Environment.ProcessPath;
        if(active is not null&&string.Equals(Path.GetFileNameWithoutExtension(active),"dotnet",StringComparison.OrdinalIgnoreCase)) return active;
        string name=OperatingSystem.IsWindows()?"dotnet.exe":"dotnet";
        string candidate=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",name));
        Check(File.Exists(candidate),"The active shared runtime's dotnet host is unavailable.");return candidate;
    }

    /// <summary>Reads one small versioned child-control line under a finite wall-duration deadline.</summary>
    private static string ReadProtocol(TextReader reader,TimeSpan timeout)
    {
        string? line=reader.ReadLineAsync().WaitAsync(timeout).GetAwaiter().GetResult();
        Check(line is not null&&line.Length<=128,"bounded child protocol line or EOF");return line!;
    }

    /// <summary>Flushes one exact child-control command without borrowing the child's backend worker or native socket.</summary>
    private static void WriteProtocol(StreamWriter writer,string line)
    {
        writer.WriteLineAsync(line).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }

    /// <summary>Drains child diagnostics without unbounded retained output, so an error pipe cannot deadlock final process cleanup.</summary>
    private static async Task<string> DrainBounded(StreamReader reader,int maximum)
    {
        var text=new StringBuilder();char[] buffer=new char[512];int read;
        while((read=await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false))!=0)
        {int take=Math.Min(read,maximum-text.Length);if(take>0)text.Append(buffer,0,take);}
        return text.ToString();
    }

    /// <summary>Builds a deterministic full IPX EtherType frame bound to this own LAN exchange and phase.</summary>
    private static byte[] IpxFrame(int size,byte phase,Guid nonce,byte source)
    {
        Check(size is 60 or 1514,"bounded own IPX fixture size");byte[] bytes=Enumerable.Range(0,size).Select(i=>(byte)(i*37+73)).ToArray();
        bytes.AsSpan(0,6).Fill(255);new byte[]{2,0x44,0x4f,0x53,0x45,source}.CopyTo(bytes,6);Put(bytes,12,0x8137);
        Check(nonce.TryWriteBytes(bytes.AsSpan(14,16)),"own frame nonce extent");bytes[30]=phase;return bytes;
    }

    /// <summary>Filters only this own bounded exchange before its complete frame is independently compared.</summary>
    private static bool IsOwnFrame(byte[] bytes,Guid nonce,byte phase,int size) => bytes.Length==size&&U16(bytes,12)==0x8137&&bytes[30]==phase&&
        bytes.AsSpan(14,16).SequenceEqual(nonce.ToByteArray());

    /// <summary>Checks actual ARP bytes from the selected virtual gateway for a leased local guest.</summary>
    private static void CheckGateway(IEthernetBackend backend,byte[] mac,byte[] guest,byte[] gateway,byte[] gatewayMac)
    {
        backend.Send(Arp(mac,new byte[6],guest,gateway,1));
        byte[] reply=Wait(backend,p=>p.Length>=42&&U16(p,12)==0x806&&U16(p,20)==2&&p.AsSpan(32,6).SequenceEqual(mac));
        Check(reply.AsSpan(22,6).SequenceEqual(gatewayMac)&&reply.AsSpan(28,4).SequenceEqual(gateway)&&reply.AsSpan(38,4).SequenceEqual(guest),"all leased guest/gateway ARP fields");
    }

    /// <summary>Checks all UDP bytes in both directions through the virtual gateway and an explicitly bound loopback server.</summary>
    private static void UdpRoundTrip(IEthernetBackend backend,byte[] mac,byte[] guest,byte[] gateway,byte[] gatewayMac)
    {
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        byte[] payload=Enumerable.Range(0,257).Select(i=>(byte)(i*73+19)).ToArray();backend.Send(Udp(mac,guest,gateway,27000,port,payload,gatewayMac));
        var read=udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(read.Buffer.SequenceEqual(payload)&&IPAddress.IsLoopback(read.RemoteEndPoint.Address),"all UDP bytes reached controlled loopback");
        udp.Send(payload,payload.Length,read.RemoteEndPoint);
        byte[] reply=Wait(backend,p=>p.Length>=42+payload.Length&&U16(p,12)==0x800&&p[23]==17&&U16(p,34)==port&&U16(p,36)==27000);
        Check(U16(reply,38)==8+payload.Length&&reply.AsSpan(42,payload.Length).SequenceEqual(payload),"all UDP return bytes and actual length");
    }

        /// <summary>Requires one actual packet, resource or cleanup invariant under the explicit native runner.</summary>
        private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        /// <summary>Reads one independent network-order field from the checked packet extent.</summary>
        private static ushort U16(byte[] p,int i)=>BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i,2));
        /// <summary>Writes one explicit network-order field while building a controlled packet.</summary>
        private static void Put(byte[] p,int i,int value)=>BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(i,2),(ushort)value);
        /// <summary>Waits finitely for one actual matching packet without depending on adjustable host date/time.</summary>
        private static byte[] Wait(IEthernetBackend backend,Func<byte[],bool> wanted)
        {
         var timer=Stopwatch.StartNew();
         while(timer.Elapsed<TimeSpan.FromSeconds(5)){while(backend.TryReceive(out var p)){if(wanted(p))return p;}if(!backend.IsConnected)throw new IOException(backend.Error);Thread.Sleep(2);}
         throw new TimeoutException("No expected actual frame: "+backend.Error);
        }
        /// <summary>Builds the exact independent ARP request or reply bytes for a controlled local guest.</summary>
        private static byte[] Arp(byte[] mac,byte[] destination,byte[] source,byte[] target,int operation)
        {
         byte[] b=new byte[42];b.AsSpan(0,6).Fill(255);mac.CopyTo(b,6);Put(b,12,0x806);Put(b,14,1);Put(b,16,0x800);b[18]=6;b[19]=4;Put(b,20,operation);mac.CopyTo(b,22);source.CopyTo(b,28);destination.CopyTo(b,32);target.CopyTo(b,38);return b;
        }
        /// <summary>Computes an independent Internet checksum, including the final odd byte and end-around carry.</summary>
        private static ushort Checksum(ReadOnlySpan<byte> value)
        {
         uint sum=0;for(int i=0;i<value.Length;i+=2)sum+=(uint)((value[i]<<8)|(i+1<value.Length?value[i+1]:0));while((sum>>16)!=0)sum=(sum&65535)+(sum>>16);return (ushort)~sum;
        }
        /// <summary>Builds an IPv4/UDP packet for the explicitly selected virtual gateway or simulated DHCP broadcast.</summary>
        private static byte[] Udp(byte[] mac,byte[] source,byte[] target,int from,int to,byte[] payload,byte[]? destination=null)
        {
         byte[] b=new byte[42+payload.Length];(destination??new byte[]{255,255,255,255,255,255}).CopyTo(b,0);mac.CopyTo(b,6);Put(b,12,0x800);b[14]=0x45;Put(b,16,28+payload.Length);b[22]=64;b[23]=17;source.CopyTo(b,26);target.CopyTo(b,30);Put(b,24,Checksum(b.AsSpan(14,20)));Put(b,34,from);Put(b,36,to);Put(b,38,8+payload.Length);payload.CopyTo(b,42);return b;
        }
        /// <summary>Performs one actual bounded DHCP exchange against the selected virtual LAN's single gateway.</summary>
        private static (byte[] Address,int Kind) Dhcp(IEthernetBackend backend,byte[] mac,uint xid,int kind,byte[]? requested)
        {
         var bytes=new List<byte>(new byte[240]);bytes[0]=1;bytes[1]=1;bytes[2]=6;bytes[10]=0x80;
         byte[] id=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(id,xid);for(int i=0;i<4;i++)bytes[4+i]=id[i];for(int i=0;i<6;i++)bytes[28+i]=mac[i];
         bytes[236]=99;bytes[237]=130;bytes[238]=83;bytes[239]=99;
         bytes.AddRange(new byte[]{53,1,(byte)kind,61,7,1});bytes.AddRange(mac);bytes.AddRange(new byte[]{55,3,1,3,6});
         if(requested is not null){bytes.AddRange(new byte[]{50,4});bytes.AddRange(requested);bytes.AddRange(new byte[]{54,4,10,0,2,2});}bytes.Add(255);
         backend.Send(Udp(mac,new byte[4],new byte[]{255,255,255,255},68,67,bytes.ToArray()));
         var packet=Wait(backend,p=>p.Length>=286&&U16(p,12)==0x800&&p[23]==17&&U16(p,34)==67&&BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(46,4))==xid);
         Check(packet[42]==2 && packet.AsSpan(70,6).SequenceEqual(mac),"actual BOOTP hardware address");
         int message=0;for(int i=282;i<packet.Length&&packet[i]!=255;){if(packet[i]==0){i++;continue;}int option=packet[i++],length=packet[i++];Check(i+length<=packet.Length,"bounded DHCP options");if(option==53){Check(length==1,"DHCP type length");message=packet[i];}i+=length;}
         return(packet.AsSpan(58,4).ToArray(),message);
        }

        /// <summary>Performs an independent guest TCP stream exchange against an explicitly bound loopback server.</summary>
        private static void TcpRoundTrip(IEthernetBackend backend,byte[] mac,byte[] guest,byte[] gateway,byte[] gatewayMac)
        {
         using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));
         using var server=new TcpListener(IPAddress.Loopback,0);server.Start();
         int port=((IPEndPoint)server.LocalEndpoint).Port;
         byte[] payload=Enumerable.Range(0,1027).Select(i=>(byte)(i*29+7)).ToArray();
         byte[] expected=payload.Select(b=>(byte)(b^0xa5)).ToArray();
         var echo=Task.Run(async()=>{using var client=await server.AcceptTcpClientAsync(cancel.Token);byte[] got=new byte[payload.Length];await client.GetStream().ReadExactlyAsync(got,cancel.Token);Check(got.SequenceEqual(payload),"all TCP bytes reached controlled loopback");await client.GetStream().WriteAsync(expected,cancel.Token);});
         try
         {
         uint seq=0x12345678;
         backend.Send(Tcp(mac,guest,gateway,30001,port,seq,0,2,[],gatewayMac));
         // Match this controlled TCP connection before checking its exact payload and sequence.
         bool Match(byte[] p)=>p.Length>=54&&U16(p,12)==0x800&&p[23]==6&&U16(p,34)==port&&U16(p,36)==30001;
         byte[] syn=Wait(backend,p=>Match(p)&&(p[47]&0x12)==0x12);
         Check(BinaryPrimitives.ReadUInt32BigEndian(syn.AsSpan(42,4))==seq+1,"TCP SYN acknowledgement");
         uint remote=BinaryPrimitives.ReadUInt32BigEndian(syn.AsSpan(38,4))+1;
         backend.Send(Tcp(mac,guest,gateway,30001,port,seq+1,remote,0x10,[],gatewayMac));
         backend.Send(Tcp(mac,guest,gateway,30001,port,seq+1,remote,0x18,payload,gatewayMac));
         var assembled=new byte[1027];var seen=new bool[1027];int count=0;bool fin=false;var trace=new List<object>();
         var timer=Stopwatch.StartNew();
         while(timer.Elapsed<TimeSpan.FromSeconds(6) && (count!=1027||!fin))
         {
          if(!backend.TryReceive(out var packet)){Thread.Sleep(2);continue;}
          if(U16(packet,12)==0x806){trace.Add(new {arp=Convert.ToHexString(packet)});if(U16(packet,20)==1&&packet.AsSpan(38,4).SequenceEqual(guest)){var reply=Arp(mac,packet.AsSpan(22,6).ToArray(),guest,packet.AsSpan(28,4).ToArray(),2);packet.AsSpan(22,6).CopyTo(reply);backend.Send(reply);}continue;}
          if(!Match(packet)){trace.Add(new {other=Convert.ToHexString(packet.AsSpan(0,Math.Min(64,packet.Length)))});continue;}
          int ip=(packet[14]&15)*4;int tcp=(packet[14+ip+12]>>4)*4;int offset=14+ip+tcp;int length=U16(packet,16)-ip-tcp;
          uint rseq=BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(14+ip+4,4));trace.Add(new {length,seq=rseq,flags=packet[14+ip+13],ack=BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(14+ip+8,4))});
          for(int i=0;i<length;i++){long index=(long)rseq-remote+i;Check(index>=0&&index<assembled.Length,"bounded TCP sequence");assembled[index]=packet[offset+i];if(!seen[index]){seen[index]=true;count++;}}
          if((packet[14+ip+13]&1)!=0)fin=true;
          int contiguous=0;while(contiguous<seen.Length&&seen[contiguous])contiguous++;
          backend.Send(Tcp(mac,guest,gateway,30001,port,seq+1+1027,remote+(uint)contiguous+(fin?1u:0u),0x10,[],gatewayMac));
         }
         Check(count==1027&&assembled.SequenceEqual(expected)&&fin,"all TCP reply bytes and natural server FIN "+JsonSerializer.Serialize(new {count,fin,echo=echo.Status,error=backend.Error,trace}));
         backend.Send(Tcp(mac,guest,gateway,30001,port,seq+1+1027,remote+1028,0x11,[],gatewayMac));
         _=Wait(backend,p=>Match(p)&&(p[47]&0x10)!=0&&BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(42,4))==seq+1029);
         echo.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
         }
         finally
         {
          cancel.Cancel();server.Stop();
          try {echo.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();}
          catch(Exception error) when(error is OperationCanceledException or SocketException or IOException) { }
         }
        }
        /// <summary>Builds explicit guest IPv4/TCP bytes with an independently computed pseudo-header checksum.</summary>
        private static byte[] Tcp(byte[] mac,byte[] source,byte[] target,int from,int to,uint sequence,uint ack,int flags,byte[] payload,byte[] destination)
        {
         byte[] b=new byte[54+payload.Length];destination.CopyTo(b,0);mac.CopyTo(b,6);Put(b,12,0x800);b[14]=0x45;Put(b,16,40+payload.Length);b[22]=64;b[23]=6;source.CopyTo(b,26);target.CopyTo(b,30);Put(b,24,Checksum(b.AsSpan(14,20)));Put(b,34,from);Put(b,36,to);
         BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(38,4),sequence);BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(42,4),ack);b[46]=0x50;b[47]=(byte)flags;Put(b,48,65535);payload.CopyTo(b,54);
         byte[] pseudo=new byte[12+20+payload.Length];source.CopyTo(pseudo,0);target.CopyTo(pseudo,4);pseudo[9]=6;Put(pseudo,10,20+payload.Length);b.AsSpan(34).CopyTo(pseudo.AsSpan(12));Put(b,50,Checksum(pseudo));return b;
        }
}
