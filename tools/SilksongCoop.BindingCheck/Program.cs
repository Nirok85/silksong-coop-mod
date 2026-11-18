using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SilksongCoop.BindingCheck <game Managed directory>");
    return 2;
}

using var stream = File.OpenRead(Path.Combine(args[0], "Assembly-CSharp.dll"));
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();

var methods = new (string Type, string Name, int Parameters)[]
{
    ("CurrencyManager", "ChangeCurrency", 3),
    ("CurrencyManager", "ProcessCurrency", 3),
    ("CurrencyManager", "LateUpdate", 0),
    ("GameManager", "BeginSceneTransition", 1),
    ("GameManager", "ReturnToMainMenu", -1),
    ("GameManager", "PlayerDead", 1),
    ("GameManager", "ReadyForRespawn", 1),
    ("PlayerData", "SetBenchRespawn", 4),
    ("HealthManager", "Hit", 1),
    ("HealthManager", "Die", 8)
};

int missing = 0;
foreach (var (type, name, parameters) in methods)
{
    var definition = metadata.TypeDefinitions
        .Select(metadata.GetTypeDefinition)
        .FirstOrDefault(t => metadata.GetString(t.Name) == type);
    if (!definition.Name.IsNil && definition.GetMethods()
        .Select(metadata.GetMethodDefinition)
        .Any(m => metadata.GetString(m.Name) == name &&
                  (parameters < 0 || m.GetParameters().Count(p => metadata.GetParameter(p).SequenceNumber != 0) == parameters)))
    {
        continue;
    }

    Console.Error.WriteLine($"Missing game method: {type}.{name} ({parameters} arguments)");
    missing++;
}

var currency = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
    .FirstOrDefault(t => metadata.GetString(t.Name) == "CurrencyManager");
if (currency.Name.IsNil || !currency.GetFields().Select(metadata.GetFieldDefinition)
    .Any(f => metadata.GetString(f.Name) == "updateQueued"))
{
    Console.Error.WriteLine("Missing game field: CurrencyManager.updateQueued");
    missing++;
}

var playerData = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
    .FirstOrDefault(t => metadata.GetString(t.Name) == "PlayerData");
foreach (var name in new[] { "silkMax", "silkSpoolParts", "heartPieces", "maxHealthBase" })
{
    var field = playerData.Name.IsNil ? default : playerData.GetFields().Select(metadata.GetFieldDefinition)
        .FirstOrDefault(f => metadata.GetString(f.Name) == name);
    var signature = field.Name.IsNil ? Array.Empty<byte>() : metadata.GetBlobBytes(field.Signature);
    // Field signature 0x06, Int32 element 0x08. Flag 0x80 is NonSerialized.
    if (signature.Length != 2 || signature[0] != 0x06 || signature[1] != 0x08 || ((int)field.Attributes & 0x80) != 0)
    {
        Console.Error.WriteLine($"Missing or incompatible persistent Int32 field: PlayerData.{name}");
        missing++;
    }
}

if (missing == 0) Console.WriteLine($"Verified {methods.Length} game methods, the currency queue field and four persistent progression fields.");
return missing == 0 ? 0 : 1;
