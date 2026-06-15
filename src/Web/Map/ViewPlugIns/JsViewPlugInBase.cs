// <copyright file="JsViewPlugInBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Map.ViewPlugIns;

using Microsoft.JSInterop;

/// <summary>
/// Base class for a javascript map view plugin.
/// </summary>
public abstract class JsViewPlugInBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsViewPlugInBase"/> class.
    /// </summary>
    /// <param name="jsRuntime">The js runtime.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="jsMethodName">Name of the js method.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    protected JsViewPlugInBase(IJSRuntime jsRuntime, ILoggerFactory loggerFactory, string jsMethodName, CancellationToken cancellationToken)
    {
        this.JsRuntime = jsRuntime;
        this.JsMethodName = jsMethodName;
        this.CancellationToken = cancellationToken;
        this.Logger = loggerFactory.CreateLogger(this.GetType());
    }

    /// <summary>
    /// Gets the logger for this class.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the <see cref="IJSRuntime" /> to call the <see cref="JsMethodName" />.
    /// </summary>
    protected IJSRuntime JsRuntime { get; }

    /// <summary>
    /// Gets the method name of the javascript function which implements this view plugin logic on the client side.
    /// </summary>
    protected string JsMethodName { get; }

    /// <summary>
    /// Gets the <see cref="CancellationToken" /> which stops calling the <see cref="JsMethodName" /> after the map object has been removed.
    /// </summary>
    protected CancellationToken CancellationToken { get; }

    /// <summary>
    /// Invokes the <see cref="JsMethodName" /> with the specified parameters.
    /// </summary>
    /// <param name="args">The parameters for the function call.</param>
    /// <returns>The <see cref="ValueTask"/> of this async operation.</returns>
    protected async ValueTask InvokeAsync(params object[] args)
    {
        const int maximumRetries = 10;
        var tryAgain = true;
        for (int i = 0; i < maximumRetries && tryAgain && !this.CancellationToken.IsCancellationRequested; i++)
        {
            try
            {
                await this.JsRuntime.InvokeVoidAsync(this.JsMethodName, this.CancellationToken, args).ConfigureAwait(false);
                tryAgain = false;
            }
            catch (TaskCanceledException)
            {
                tryAgain = false;
                this.Logger.LogWarning("JS interop call to {Method} canceled", this.JsMethodName);
            }
            catch (JSException e)
                when (e.Message.StartsWith("Could not find '") && (e.Message.Contains("' in 'window'.") || e.Message.Contains("' was undefined).")))
            {
                this.Logger.LogWarning("JS interop: {Method} not found (attempt {Attempt}): {Message}", this.JsMethodName, i + 1, e.Message);
                await Task.Delay(500, this.CancellationToken).ConfigureAwait(false);
            }
            catch (JSException e)
            {
                this.Logger.LogWarning("JS interop: {Method} JSException (attempt {Attempt}): {ErrorType}: {Message}", this.JsMethodName, i + 1, e.GetType().Name, e.Message);
                if (i < maximumRetries - 1)
                {
                    await Task.Delay(500, this.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    tryAgain = false;
                    this.Logger.LogWarning("JS interop call to {method} failed after {retries} retries. Params: {args}", this.JsMethodName, maximumRetries, string.Join(';', args));
                }
            }
            catch (Exception e)
            {
                this.Logger.LogWarning("JS interop: {Method} unexpected exception (attempt {Attempt}): {ErrorType}: {Message}\n{StackTrace}", this.JsMethodName, i + 1, e.GetType().Name, e.Message, e.StackTrace);
                tryAgain = false;
            }
        }
    }
}