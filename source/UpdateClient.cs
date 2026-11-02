using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed class PreparedUpdateFile
{
    private readonly string relativePath,sha256;
    private readonly long length;
    internal string RelativePath { get { return relativePath; } }
    internal string Sha256 { get { return sha256; } }
    internal long Length { get { return length; } }
    internal PreparedUpdateFile(string relativePath,string sha256,long length)
    {
        this.relativePath=relativePath; this.sha256=sha256; this.length=length;
    }
}

internal sealed class PreparedUpdate
{
    internal Version Version { get; private set; }
    internal string Directory { get; private set; }
    internal string ZipSha256 { get; private set; }
    internal ReadOnlyCollection<PreparedUpdateFile> Files { get; private set; }
    internal PreparedUpdate(Version version,string directory,string zipSha256,IEnumerable<PreparedUpdateFile> files)
    {
        if(files==null) throw new ArgumentNullException("files");
        var snapshot=new List<PreparedUpdateFile>();
        foreach(var file in files)
        {
            if(file==null) throw new ArgumentException("The prepared update manifest contains a null file.","files");
            snapshot.Add(file);
        }
        Version=version; Directory=directory; ZipSha256=zipSha256;
        Files=snapshot.AsReadOnly();
    }
}

// Downloads only the public release belonging to this project's fixed GitHub
// repository. Preparing an update never writes the installation or starts code.
internal static class UpdateClient
{
    private const string LatestUrl="https://api.github.com/repos/dreamion08/azeroth-survivors-speed-trainer/releases/latest";
    private const string DownloadPrefix="/dreamion08/azeroth-survivors-speed-trainer/releases/download/";
    private const long MaxZipBytes=25L*1024*1024;
    private const int MaxMetadataBytes=1024*1024;
    private const int MaxChecksumBytes=1024;
    private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
    internal static readonly string[] RuntimeFiles={
        "Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe",
        "build-info.json","README.md","LICENSE","RELEASE_NOTES.md","SHA256SUMS.txt",
        "docs/full-preview.png","docs/mini-preview.png"
    };
    private static readonly string[] RequiredFiles={
        "Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe","build-info.json","SHA256SUMS.txt"
    };
    private static readonly string[] RequiredBinaryFiles={
        "Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe"
    };

    internal static Version ParseStableVersion(string tag)
    {
        Version value;
        if(tag==null || !Regex.IsMatch(tag,@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")
            || !Version.TryParse(tag.Substring(1),out value))
            throw new InvalidDataException("The release does not have a supported stable version tag.");
        return value;
    }
    private static Version FourParts(Version value)
    {
        return new Version(value.Major,value.Minor,Math.Max(0,value.Build),Math.Max(0,value.Revision));
    }
    internal static Uri ValidateAssetUrl(string url,string tag,string assetName)
    {
        Uri uri;
        if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || !IsHttps(uri)
            || !String.Equals(uri.Host,"github.com",StringComparison.OrdinalIgnoreCase)
            || !String.Equals(uri.AbsolutePath,DownloadPrefix+tag+"/"+assetName,StringComparison.Ordinal)
            || uri.Query.Length!=0 || uri.Fragment.Length!=0)
            throw new InvalidDataException("The release asset is not hosted at the expected project download URL.");
        return uri;
    }
    private static bool IsHttps(Uri uri)
    {
        return uri.Scheme==Uri.UriSchemeHttps && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo);
    }
    private static bool AllowedRedirect(Uri uri,Uri initial)
    {
        if(!IsHttps(uri) || !String.IsNullOrEmpty(uri.Fragment)) return false;
        if(String.Equals(uri.Host,initial.Host,StringComparison.OrdinalIgnoreCase))
            return String.Equals(uri.AbsolutePath,initial.AbsolutePath,StringComparison.Ordinal);
        // GitHub release assets use signed URLs on its own object-storage CDN.
        return String.Equals(uri.Host,"release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase)
            || String.Equals(uri.Host,"objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase)
            || String.Equals(uri.Host,"github-releases.githubusercontent.com",StringComparison.OrdinalIgnoreCase);
    }
    private static HttpWebResponse OpenResponse(Uri initial,string userAgent)
    {
        Uri current=initial;
        for(int redirect=0;redirect<=5;redirect++)
        {
            var request=(HttpWebRequest)WebRequest.Create(current);
            request.Method="GET";
            request.UserAgent=userAgent;
            request.Accept=initial.Host=="api.github.com" ? "application/vnd.github+json" : "application/octet-stream";
            request.AllowAutoRedirect=false;
            request.Timeout=15000;
            request.ReadWriteTimeout=15000;
            request.KeepAlive=false;
            request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            var response=(HttpWebResponse)request.GetResponse();
            int code=(int)response.StatusCode;
            if(code==200) return response;
            if(code==301 || code==302 || code==303 || code==307 || code==308)
            {
                string location=response.Headers[HttpResponseHeader.Location];
                response.Dispose();
                Uri next;
                if(String.IsNullOrEmpty(location) || !Uri.TryCreate(current,location,out next) || !AllowedRedirect(next,initial))
                    throw new InvalidDataException("The update download redirected outside GitHub's permitted HTTPS hosts.");
                current=next;
                continue;
            }
            response.Dispose();
            throw new InvalidDataException("The update server returned an unexpected response.");
        }
        throw new InvalidDataException("The update download redirected too many times.");
    }
    private static long CopyBounded(Stream input,Stream output,long maximum)
    {
        var buffer=new byte[65536];
        long total=0;
        int count;
        while((count=input.Read(buffer,0,buffer.Length))!=0)
        {
            if(total>maximum-count) throw new InvalidDataException("The update exceeds the permitted download or extraction size.");
            output.Write(buffer,0,count); total+=count;
        }
        return total;
    }
    private static byte[] DownloadBytes(Uri uri,int maximum,string userAgent)
    {
        using(var response=OpenResponse(uri,userAgent))
        using(var input=response.GetResponseStream())
        using(var output=new MemoryStream())
        {
            if(response.ContentLength>maximum) throw new InvalidDataException("Update metadata is too large.");
            CopyBounded(input,output,maximum);
            return output.ToArray();
        }
    }
    private static void DownloadFile(Uri uri,string path,long maximum,string userAgent)
    {
        using(var response=OpenResponse(uri,userAgent))
        using(var input=response.GetResponseStream())
        using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            if(response.ContentLength>maximum) throw new InvalidDataException("The update archive is too large.");
            CopyBounded(input,output,maximum);
        }
    }
    private static Dictionary<string,object> Json(string text)
    {
        if(text.Length>MaxMetadataBytes) throw new InvalidDataException("Update metadata is too large.");
        if(text.Length!=0 && text[0]=='\uFEFF') text=text.Substring(1);
        var serializer=new JavaScriptSerializer {MaxJsonLength=MaxMetadataBytes,RecursionLimit=32};
        var value=serializer.DeserializeObject(text) as Dictionary<string,object>;
        if(value==null) throw new InvalidDataException("Update metadata has an invalid JSON structure.");
        return value;
    }
    private static string StringValue(Dictionary<string,object> value,string key)
    {
        object result;
        if(!value.TryGetValue(key,out result) || !(result is string)) throw new InvalidDataException("Update metadata is missing "+key+".");
        return (string)result;
    }
    private static bool BooleanValue(Dictionary<string,object> value,string key)
    {
        object result;
        if(!value.TryGetValue(key,out result) || !(result is bool)) throw new InvalidDataException("Update metadata is missing "+key+".");
        return (bool)result;
    }
    private static Dictionary<string,object> FindAsset(Dictionary<string,object> release,string name)
    {
        object raw;
        if(!release.TryGetValue("assets",out raw) || !(raw is object[])) throw new InvalidDataException("The release has no downloadable assets.");
        Dictionary<string,object> found=null;
        foreach(object item in (object[])raw)
        {
            var asset=item as Dictionary<string,object>;
            if(asset==null) throw new InvalidDataException("The release asset list is invalid.");
            if(!String.Equals(StringValue(asset,"name"),name,StringComparison.Ordinal)) continue;
            if(found!=null) throw new InvalidDataException("The release contains duplicate update assets.");
            found=asset;
        }
        if(found==null) throw new InvalidDataException("The release is missing its Windows update archive or checksum.");
        return found;
    }
    internal static string ParseChecksumText(string text,string expectedFile)
    {
        var match=Regex.Match(text??"",@"\A([0-9a-fA-F]{64}) [ *]([^\r\n]+)\r?\n?\z");
        if(!match.Success || !String.Equals(match.Groups[2].Value,expectedFile,StringComparison.Ordinal))
            throw new InvalidDataException("The release checksum does not name the exact update archive.");
        return match.Groups[1].Value.ToLowerInvariant();
    }
    private static string OptionalApiDigest(Dictionary<string,object> asset)
    {
        object raw;
        if(!asset.TryGetValue("digest",out raw) || raw==null) return null;
        string digest=raw as string;
        if(String.IsNullOrEmpty(digest)) return null;
        if(!Regex.IsMatch(digest,@"\Asha256:[a-fA-F0-9]{64}\z")) throw new InvalidDataException("The release API supplied an unsupported asset digest.");
        return digest.Substring(7).ToLowerInvariant();
    }
    private static string Hash(Stream input)
    {
        using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
    }
    private static string HashFile(string path)
    {
        using(var file=File.OpenRead(path)) return Hash(file);
    }
    private static void Report(Action<string> callback,string message) { if(callback!=null) callback(message); }

    internal static PreparedUpdate CheckAndDownload(Version current,string stagingRoot,Action<string> status)
    {
        if(current==null) throw new ArgumentNullException("current");
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        string userAgent="AzerothSurvivorsSpeedTrainer/"+current.ToString();
        Report(status,"Checking for updates…");
        var release=Json(Utf8.GetString(DownloadBytes(new Uri(LatestUrl),MaxMetadataBytes,userAgent)));
        if(BooleanValue(release,"draft") || BooleanValue(release,"prerelease")) throw new InvalidDataException("Only published stable releases can be installed.");
        DateTimeOffset published;
        if(!DateTimeOffset.TryParse(StringValue(release,"published_at"),CultureInfo.InvariantCulture,DateTimeStyles.None,out published))
            throw new InvalidDataException("The update release has not been published.");
        string tag=StringValue(release,"tag_name");
        Version version=ParseStableVersion(tag);
        if(FourParts(version).CompareTo(FourParts(current))<=0) { Report(status,"You're up to date."); return null; }
        string zipName="Azeroth-Survivors-Speed-Trainer-v"+version.ToString()+"-win-x64.zip";
        var zipAsset=FindAsset(release,zipName);
        var checksumAsset=FindAsset(release,zipName+".sha256");
        Uri zipUrl=ValidateAssetUrl(StringValue(zipAsset,"browser_download_url"),tag,zipName);
        Uri checksumUrl=ValidateAssetUrl(StringValue(checksumAsset,"browser_download_url"),tag,zipName+".sha256");
        string expected=ParseChecksumText(Utf8.GetString(DownloadBytes(checksumUrl,MaxChecksumBytes,userAgent)),zipName);
        string apiDigest=OptionalApiDigest(zipAsset);
        if(apiDigest!=null && !String.Equals(apiDigest,expected,StringComparison.Ordinal)) throw new InvalidDataException("The release archive checksums disagree.");
        string root=SafeRoot(stagingRoot,true);
        string download=Path.Combine(root,".download-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            Report(status,"Downloading version "+version.ToString()+"…");
            DownloadFile(zipUrl,download,MaxZipBytes,userAgent);
            Report(status,"Verifying and preparing update…");
            return PrepareFromZip(download,version,expected,root);
        }
        finally { if(File.Exists(download)) File.Delete(download); }
    }

    private static string SafeRoot(string path,bool create)
    {
        if(String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length<3 || path.StartsWith(@"\\",StringComparison.Ordinal))
            throw new InvalidDataException("Updates must be staged in a local absolute folder.");
        string root=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(root.Length<=Path.GetPathRoot(root).Length) throw new InvalidDataException("The update staging folder cannot be a drive root.");
        EnsureNoReparseParents(root);
        if(create) Directory.CreateDirectory(root);
        EnsureNoReparseParents(root);
        return root;
    }
    private static void EnsureNoReparseParents(string path)
    {
        var directory=new DirectoryInfo(Path.GetFullPath(path));
        while(directory!=null)
        {
            if(directory.Exists && (directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Update folders cannot contain junctions or symbolic links.");
            directory=directory.Parent;
        }
    }
    private static string Destination(string root,string relative)
    {
        string result=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!result.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An update path escapes the staging folder.");
        return result;
    }
    private static bool IsSymlinkOrSpecial(ZipArchiveEntry entry,bool directory)
    {
        int type=(entry.ExternalAttributes>>16)&0xF000;
        if((entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0) return true;
        return type!=0 && type!=(directory ? 0x4000 : 0x8000);
    }
    private static void ValidateExtracted(string directory,Version version)
    {
        string metadataPath=Destination(directory,"build-info.json");
        string sumsPath=Destination(directory,"SHA256SUMS.txt");
        if(new FileInfo(metadataPath).Length>MaxMetadataBytes || new FileInfo(sumsPath).Length>MaxMetadataBytes)
            throw new InvalidDataException("The update metadata or file checksum list is too large.");
        var metadata=Json(Utf8.GetString(File.ReadAllBytes(metadataPath)));
        Version recorded=ParseStableVersion("v"+StringValue(metadata,"version"));
        if(FourParts(recorded)!=FourParts(version) || StringValue(metadata,"platform")!="win-x64")
            throw new InvalidDataException("The update build metadata does not match this release version and platform.");
        string exe=Destination(directory,"Azeroth-Speed.exe");
        string fileVersion=FileVersionInfo.GetVersionInfo(exe).FileVersion;
        Version actual;
        if(!Version.TryParse(fileVersion,out actual) || FourParts(actual)!=FourParts(version))
            throw new InvalidDataException("The trainer executable version does not match the release.");
        string sums=Utf8.GetString(File.ReadAllBytes(sumsPath));
        if(sums.Length>MaxMetadataBytes) throw new InvalidDataException("The update file checksum list is too large.");
        var allowed=new HashSet<string>(RuntimeFiles,StringComparer.Ordinal);
        var named=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using(var reader=new StringReader(sums))
        {
            string line;
            while((line=reader.ReadLine())!=null)
            {
                if(line.Length==0) continue;
                var match=Regex.Match(line,@"\A([0-9a-fA-F]{64}) [ *](.+)\z");
                if(!match.Success) throw new InvalidDataException("The update file checksum list is malformed.");
                string relative=match.Groups[2].Value;
                if(!allowed.Contains(relative) || relative=="SHA256SUMS.txt" || !named.Add(relative))
                    throw new InvalidDataException("The update checksum list contains an unexpected or duplicate path.");
                string file=Destination(directory,relative);
                if(!File.Exists(file) || !String.Equals(HashFile(file),match.Groups[1].Value,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An update file failed its SHA-256 checksum.");
            }
        }
        foreach(string required in RequiredBinaryFiles)
            if(!named.Contains(required)) throw new InvalidDataException("The checksum list does not cover every update executable.");
    }
    private static void DeleteOwnedCandidate(string directory)
    {
        if(!Directory.Exists(directory)) return;
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) { Directory.Delete(directory,false); return; }
        foreach(string file in Directory.GetFiles(directory)) File.Delete(file);
        foreach(string child in Directory.GetDirectories(directory)) DeleteOwnedCandidate(child);
        Directory.Delete(directory,false);
    }

    internal static PreparedUpdate PrepareFromZip(string zipPath,Version version,string zipSha256,string stagingRoot)
    {
        if(version==null) throw new ArgumentNullException("version");
        if(zipSha256==null || !Regex.IsMatch(zipSha256,@"\A[0-9a-fA-F]{64}\z")) throw new InvalidDataException("The update archive checksum is invalid.");
        string candidate=null;
        using(var image=new FileStream(zipPath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(image.Length>MaxZipBytes) throw new InvalidDataException("The update archive is too large.");
            if(!String.Equals(Hash(image),zipSha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive failed its SHA-256 checksum.");
            image.Position=0;
            using(var archive=new ZipArchive(image,ZipArchiveMode.Read,true))
            {
                var allowed=new HashSet<string>(RuntimeFiles,StringComparer.Ordinal);
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries=new List<ZipArchiveEntry>();
                long expanded=0;
                if(archive.Entries.Count>RuntimeFiles.Length+1) throw new InvalidDataException("The update archive has too many entries.");
                foreach(var entry in archive.Entries)
                {
                    string name=entry.FullName;
                    bool directory=name=="docs/";
                    if(!seen.Add(name) || name.IndexOf('\\')>=0 || name.IndexOf(':')>=0 || IsSymlinkOrSpecial(entry,directory))
                        throw new InvalidDataException("The update archive contains a duplicate, linked or unsafe path.");
                    if(directory)
                    {
                        if(entry.Length!=0) throw new InvalidDataException("The update directory entry contains data.");
                        continue;
                    }
                    if(!allowed.Contains(name)) throw new InvalidDataException("The update archive contains an unexpected file path.");
                    if(entry.Length<0 || entry.Length>MaxZipBytes || expanded>MaxZipBytes-entry.Length)
                        throw new InvalidDataException("The extracted update is too large.");
                    expanded+=entry.Length;
                    entries.Add(entry);
                }
                foreach(string required in RequiredFiles)
                    if(!seen.Contains(required)) throw new InvalidDataException("The update archive is missing a required runtime file.");
                // No staging directory is made until archive checksum, names,
                // duplicates and declared extraction sizes have been checked.
                string root=SafeRoot(stagingRoot,true);
                string id=Guid.NewGuid().ToString("N");
                candidate=Path.Combine(root,".prepare-"+id);
                string prepared=Path.Combine(root,"v"+version.ToString()+"-"+id);
                try
                {
                    if(Directory.Exists(candidate) || Directory.Exists(prepared)) throw new IOException("The update staging folder already exists.");
                    Directory.CreateDirectory(candidate);
                    EnsureNoReparseParents(candidate);
                    long written=0;
                    foreach(var entry in entries)
                    {
                        string target=Destination(candidate,entry.FullName);
                        string parent=Path.GetDirectoryName(target);
                        EnsureNoReparseParents(parent); Directory.CreateDirectory(parent); EnsureNoReparseParents(parent);
                        using(var input=entry.Open())
                        using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {
                            long count=CopyBounded(input,output,Math.Min(entry.Length,MaxZipBytes-written));
                            if(count!=entry.Length) throw new InvalidDataException("An extracted file has an invalid length.");
                            written+=count;
                        }
                    }
                    EnsureNoReparseParents(candidate);
                    ValidateExtracted(candidate,version);
                    // Retain the original verified archive's integrity evidence.
                    // Staged files remain mutable until installation; rehashing
                    // them later must not turn a changed payload into trusted data.
                    var manifest=new List<PreparedUpdateFile>();
                    foreach(var entry in entries)
                    {
                        using(var input=entry.Open())
                            manifest.Add(new PreparedUpdateFile(entry.FullName,Hash(input),entry.Length));
                    }
                    EnsureNoReparseParents(root);
                    Directory.Move(candidate,prepared);
                    candidate=null;
                    return new PreparedUpdate(version,prepared,zipSha256.ToLowerInvariant(),manifest);
                }
                finally { if(candidate!=null) DeleteOwnedCandidate(candidate); }
            }
        }
    }
}
