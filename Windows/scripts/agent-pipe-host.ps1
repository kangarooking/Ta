[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^[^\\]{1,180}$')]
  [string]$PipeName
)

$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

public static class TaCurrentUserPipeHost
{
    private const int MaxRequestCharacters = 65536;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static void Run(string pipeName)
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User;
        if (owner == null) throw new InvalidOperationException("Unable to determine the current Windows user SID.");
        bool announcedReady = false;
        while (true)
        {
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(owner);
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
            using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, MaxRequestCharacters, MaxRequestCharacters, security))
            {
                if (!announcedReady)
                {
                    Console.Out.WriteLine("READY");
                    Console.Out.Flush();
                    announcedReady = true;
                }
                pipe.WaitForConnection();
                using (StreamReader reader = new StreamReader(pipe, Utf8, false, MaxRequestCharacters, true))
                using (StreamWriter writer = new StreamWriter(pipe, Utf8, MaxRequestCharacters, true))
                {
                    writer.AutoFlush = true;
                    string request = ReadBoundedLine(reader);
                    if (request == null)
                    {
                        writer.WriteLine("{\"protocolVersion\":1,\"requestId\":\"unknown\",\"ok\":false,\"artifacts\":[],\"error\":{\"code\":\"INVALID_REQUEST\",\"message\":\"Request exceeds the 64 KB limit.\",\"retryable\":false}}");
                        continue;
                    }
                    Console.Out.WriteLine("REQUEST " + Convert.ToBase64String(Utf8.GetBytes(request)));
                    Console.Out.Flush();
                    string parentReply = Console.In.ReadLine();
                    if (parentReply == null || !parentReply.StartsWith("RESPONSE ", StringComparison.Ordinal)) continue;
                    string response = Utf8.GetString(Convert.FromBase64String(parentReply.Substring("RESPONSE ".Length)));
                    writer.WriteLine(response);
                }
            }
        }
    }

    private static string ReadBoundedLine(TextReader reader)
    {
        StringBuilder value = new StringBuilder();
        for (int index = 0; index <= MaxRequestCharacters; index++)
        {
            int current = reader.Read();
            if (current < 0) return value.Length == 0 ? null : value.ToString();
            if (current == '\n') return value.ToString().TrimEnd('\r');
            if (index == MaxRequestCharacters) return null;
            value.Append((char)current);
        }
        return null;
    }
}
'@

if (-not ('TaCurrentUserPipeHost' -as [type])) { Add-Type -TypeDefinition $source -Language CSharp }
[TaCurrentUserPipeHost]::Run($PipeName)
