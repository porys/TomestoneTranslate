using System.Threading;
using System.Threading.Tasks;

namespace Tomestone.Translate.Engines;

public interface ITranslator : System.IDisposable
{
    Task<string?> TranslateAsync(string sourceText, string targetLanguage, System.Threading.CancellationToken ct);
}
