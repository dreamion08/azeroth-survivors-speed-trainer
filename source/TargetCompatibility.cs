using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// This is a structural compatibility check, not an executable signature check.
// Actual IAT contents are checked independently by the in-process native module.
internal static class TargetCompatibility
{
    internal static void Verify(Stream image)
    {
        if(image==null) throw new ArgumentNullException("image");
        if(!image.CanRead || !image.CanSeek)
            throw new NotSupportedException("The game executable must be readable and seekable.");
        long saved=image.Position;
        try { new Inspector(image).Verify(); }
        finally { image.Position=saved; }
    }

    private sealed class Section
    {
        internal uint Address,VirtualSize,RawAddress,RawSize;
        internal ulong Span { get { return Math.Max((ulong)VirtualSize,(ulong)RawSize); } }
    }

    private sealed class Inspector
    {
        private readonly Stream stream;
        private readonly long length;
        private readonly byte[] scratch=new byte[8];
        private readonly List<Section> sections=new List<Section>();
        private uint headerSize,imageSize;

        internal Inspector(Stream image) { stream=image; length=image.Length; }
        private static InvalidDataException Bad(string detail)
        {
            return new InvalidDataException("The game executable has an unsupported or damaged PE layout: "+detail+".");
        }
        private void Read(long offset,byte[] bytes,int count)
        {
            if(offset<0 || count<0 || offset>length-count) throw Bad("file range is outside the executable");
            stream.Position=offset;
            int done=0;
            while(done<count)
            {
                int n=stream.Read(bytes,done,count-done);
                if(n==0) throw Bad("unexpected end of file");
                done+=n;
            }
        }
        private ushort U16(long offset) { Read(offset,scratch,2); return BitConverter.ToUInt16(scratch,0); }
        private uint U32(long offset) { Read(offset,scratch,4); return BitConverter.ToUInt32(scratch,0); }
        private ulong U64(long offset) { Read(offset,scratch,8); return BitConverter.ToUInt64(scratch,0); }
        private long Map(ulong rva,int count)
        {
            if(count<0 || rva>UInt32.MaxValue || rva+(ulong)count>imageSize) throw Bad("an RVA is outside the image");
            if(rva<headerSize)
            {
                if(rva+(ulong)count>headerSize) throw Bad("an RVA crosses the header boundary");
                return (long)rva;
            }
            foreach(Section section in sections)
            {
                if(rva<section.Address || rva>=(ulong)section.Address+section.Span) continue;
                ulong delta=rva-section.Address;
                if(delta+(ulong)count>section.RawSize) throw Bad("an import points into unbacked section data");
                return (long)((ulong)section.RawAddress+delta);
            }
            throw Bad("an RVA does not belong to a mapped section");
        }
        private string Ascii(ulong rva,int maximum)
        {
            var text=new StringBuilder();
            for(int i=0;i<maximum;i++)
            {
                Read(Map(rva+(ulong)i,1),scratch,1);
                byte b=scratch[0];
                if(b==0) return text.ToString();
                if(b<32 || b>126) throw Bad("an import name is not ASCII");
                text.Append((char)b);
            }
            throw Bad("an import name is not terminated");
        }
        private static bool WindowsProvider(string name)
        {
            return String.Equals(name,"kernel32.dll",StringComparison.OrdinalIgnoreCase)
                || String.Equals(name,"kernelbase.dll",StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("api-ms-win-core-profile-",StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase));
        }

        internal void Verify()
        {
            if(length<64 || U16(0)!=0x5A4D) throw Bad("DOS header missing");
            long pe=U32(60);
            if(pe<64 || pe+24>length || U32(pe)!=0x4550) throw Bad("PE header missing");
            ushort machine=U16(pe+4),sectionCount=U16(pe+6),optionalSize=U16(pe+20),characteristics=U16(pe+22);
            if(machine!=0x8664) throw new NotSupportedException("This trainer requires the 64-bit x64 Windows game executable.");
            if((characteristics&2)==0 || (characteristics&0x2000)!=0) throw Bad("the image is not a standalone executable");
            if(sectionCount==0 || sectionCount>96 || optionalSize<240 || optionalSize>4096) throw Bad("invalid header sizes");
            long optional=pe+24,sectionTable=optional+optionalSize;
            if(sectionTable+sectionCount*40L>length) throw Bad("section headers are truncated");
            if(U16(optional)!=0x20B) throw new NotSupportedException("This trainer requires a PE32+ 64-bit game executable.");
            uint directoryCount=U32(optional+108);
            if(directoryCount<2 || directoryCount>(optionalSize-112)/8 || directoryCount>16) throw Bad("invalid data directories");
            imageSize=U32(optional+56); headerSize=U32(optional+60);
            if(imageSize==0 || headerSize<sectionTable+sectionCount*40L || headerSize>length || headerSize>imageSize) throw Bad("invalid image or header extent");
            for(int i=0;i<sectionCount;i++)
            {
                long at=sectionTable+i*40L;
                var s=new Section {VirtualSize=U32(at+8),Address=U32(at+12),RawSize=U32(at+16),RawAddress=U32(at+20)};
                if(s.Span==0) continue;
                if(s.Address<headerSize || (ulong)s.Address+s.Span>imageSize) throw Bad("section exceeds image bounds");
                if(s.RawSize>0 && (s.RawAddress<headerSize || (ulong)s.RawAddress+s.RawSize>(ulong)length)) throw Bad("section exceeds file bounds");
                foreach(Section other in sections)
                {
                    if((ulong)s.Address<(ulong)other.Address+other.Span && (ulong)other.Address<(ulong)s.Address+s.Span) throw Bad("virtual sections overlap");
                    if(s.RawSize>0 && other.RawSize>0 && (ulong)s.RawAddress<(ulong)other.RawAddress+other.RawSize && (ulong)other.RawAddress<(ulong)s.RawAddress+s.RawSize) throw Bad("raw sections overlap");
                }
                sections.Add(s);
            }
            CheckImports(U32(optional+120),U32(optional+124));
            CheckIdentityMarkers();
        }

        private void CheckImports(uint address,uint size)
        {
            if(address==0 || size<40 || size>20*4096 || (ulong)address+size>imageSize)
                throw new NotSupportedException("The game does not expose a supported normal Windows import table.");
            bool terminated=false;
            int qpc=0,qpf=0;
            for(int i=0;i<size/20;i++)
            {
                long descriptor=Map((ulong)address+(ulong)i*20,20);
                uint names=U32(descriptor),time=U32(descriptor+4),forward=U32(descriptor+8),dllName=U32(descriptor+12),slots=U32(descriptor+16);
                if(names==0 && time==0 && forward==0 && dllName==0 && slots==0) { terminated=true; break; }
                if(names==0 || dllName==0 || slots==0) throw Bad("an import descriptor lacks the tables needed by the timing hook");
                if((names&7)!=0 || (slots&7)!=0) throw Bad("import thunk table is not aligned");
                string provider=Ascii(dllName,260);
                bool windows=WindowsProvider(provider),thunkTerminated=false;
                for(int j=0;j<65536;j++)
                {
                    ulong thunk=U64(Map((ulong)names+(ulong)j*8,8));
                    Map((ulong)slots+(ulong)j*8,8);
                    if(thunk==0) { thunkTerminated=true; break; }
                    if((thunk&0x8000000000000000UL)!=0)
                    {
                        if((thunk&0x7FFFFFFFFFFF0000UL)!=0) throw Bad("invalid ordinal import");
                        continue;
                    }
                    if(thunk>UInt32.MaxValue) throw Bad("an import name RVA is invalid");
                    Map(thunk,2);
                    string name=Ascii(thunk+2,512);
                    if(name=="QueryPerformanceCounter" || name=="QueryPerformanceFrequency")
                    {
                        if(!windows) throw new NotSupportedException("The game's timing import uses an unsupported provider.");
                        if(name=="QueryPerformanceCounter") qpc++; else qpf++;
                    }
                }
                if(!thunkTerminated) throw Bad("import thunk table is not terminated");
            }
            if(!terminated) throw Bad("import descriptor table is not terminated");
            if(qpc!=1 || qpf<1)
                throw new NotSupportedException("The game's timing imports changed. A single named QueryPerformanceCounter import and QueryPerformanceFrequency are required; delay-only or ordinal-only imports are unsupported.");
        }

        private void CheckIdentityMarkers()
        {
            byte[][] markers={Encoding.ASCII.GetBytes("bevy_time::"),Encoding.ASCII.GetBytes("wenilla_rogue_app")};
            bool[] found=new bool[markers.Length];
            const int chunkSize=65536;
            int overlap=0;
            foreach(byte[] marker in markers) overlap=Math.Max(overlap,marker.Length-1);
            byte[] buffer=new byte[chunkSize+overlap];
            stream.Position=0;
            int carry=0;
            while(true)
            {
                int count=stream.Read(buffer,carry,chunkSize);
                if(count==0) break;
                int total=carry+count;
                for(int m=0;m<markers.Length;m++)
                {
                    if(found[m]) continue;
                    byte[] needle=markers[m];
                    for(int i=0;i<=total-needle.Length;i++)
                    {
                        if(buffer[i]!=needle[0]) continue;
                        int j=1; while(j<needle.Length && buffer[i+j]==needle[j]) j++;
                        if(j==needle.Length) { found[m]=true; break; }
                    }
                }
                if(found[0] && found[1]) return;
                carry=Math.Min(overlap,total);
                Buffer.BlockCopy(buffer,total-carry,buffer,0,carry);
            }
            throw new NotSupportedException("The executable does not contain the expected Azeroth Survivors and Bevy timing identity markers. Its engine or executable layout may have changed.");
        }
    }
}
