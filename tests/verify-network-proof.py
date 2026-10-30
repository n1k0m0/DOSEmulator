# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently verify complete own Windows/DOS networking proof bytes, without loading a VM or opening a network."""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def require(value, message):
    """Reject a concrete byte/API mismatch instead of trusting the guest success flag."""
    if not value:
        raise AssertionError(message)


def word(data, at):
    """Read one fixed little-endian DOS proof word."""
    return struct.unpack_from('<H', data, at)[0]


def dword(data, at):
    """Read one fixed little-endian Windows proof field."""
    return struct.unpack_from('<I', data, at)[0]


def windows(data):
    """Bind every returned UDP/TCP byte, actual API count and natural socket EOF to the intended payload."""
    require(len(data)==2048 and data[:8]==b'DOSENET1', 'Windows proof signature/length')
    require((dword(data,8),dword(data,12),dword(data,16),dword(data,20))==(1,2048,99,31), 'Windows final ABI/phase/flags')
    expected={24:0,28:0,32:257,36:1,40:257,44:0,48:1027,52:1027,56:0,60:22095,1400:0}
    for offset,value in expected.items():
        require(dword(data,offset)==value, 'Actual Windows API/guard field at '+str(offset))
    payload=bytes((29*i+7)%256 for i in range(1027))
    require(data[64:321]==payload[:257], 'Every UDP returned byte')
    require(data[336:1363]==payload, 'Every TCP returned byte')
    return dict(windowsApi=True,udpBytes=257,tcpBytes=1027,naturalTcpEof=True,allPayloadBytesCompared=True,guardErrors=0)


def dos(data):
    """Bind real packet-driver API returns and both complete ARP tuples without inferring success from text output."""
    require(len(data)==256 and data[:8]==b'DOSENIC1', 'DOS proof signature/length')
    require(tuple(word(data,at) for at in (8,10,12,14,16))==(1,256,99,0,127), 'DOS final ABI/phase/result/flags')
    for at in (22,28,34,40,234,238,242):
        require(not word(data,at)&1, 'Actual packet-driver/DOS carry at '+str(at))
    require(word(data,44)>=1 and word(data,46)>=1 and word(data,52)==word(data,54)==0, 'Actual callback pair and guards')
    require(word(data,56)==64 and word(data,58)==60 and word(data,228)==6 and word(data,232)==256, 'Actual RX/TX/MAC/file lengths')
    mac=data[72:78]
    require(mac==bytes.fromhex('02444f531902'), 'Actual AH06 MAC')
    gateway=data[78:84]
    require(gateway==bytes.fromhex('52550a000202'), 'Actual gateway MAC')
    source_ip=bytes((10,0,2,15))
    gateway_ip=bytes((10,0,2,2))
    arp_header=bytes.fromhex('0806000108000604')
    tx=b'\xff'*6+mac+arp_header+b'\x00\x01'+mac+source_ip+b'\0'*6+gateway_ip+b'\0'*18
    rx=mac+gateway+arp_header+b'\x00\x02'+gateway+gateway_ip+mac+source_ip+b'\0'*22
    require(len(tx)==60 and data[96:156]==tx, 'Every actual transmitted ARP/padding byte')
    require(len(rx)==64 and data[156:220]==rx, 'Every actual received ARP/padding/prefix byte')
    require(data[220:228]==source_ip+gateway_ip and data[248:252]==b'END1', 'Actual bound IPv4 tuple and trailer')
    return dict(packetDriverApi=True,actualMac=mac.hex(':').upper(),transmittedBytes=60,receivedBytes=64,callbackOffers=word(data,44),callbackDeliveries=word(data,46),allArpBytesCompared=True,guardErrors=0,finalClose='requires independent natural DOS return; not self-attested by this file')


def main():
    """Read exactly one bounded explicit proof and publish a new metadata-only result."""
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--kind',choices=['windows95','dos'],required=True)
    parser.add_argument('--proof',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    require(args.proof.stat().st_size in (256,2048), 'Explicit proof exceeds the ABI bound')
    data=args.proof.read_bytes()
    checks=windows(data) if args.kind=='windows95' else dos(data)
    result=dict(passed=True,kind=args.kind,proofBytes=len(data),proofSha256=hashlib.sha256(data).hexdigest().upper(),checks=checks)
    with args.output.open('x',encoding='utf-8') as stream:
        json.dump(result,stream,indent=2)
    print(json.dumps(result))


if __name__=='__main__':
    main()
