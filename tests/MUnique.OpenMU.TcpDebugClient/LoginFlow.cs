namespace MUnique.OpenMU.TcpDebugClient;

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Network.Xor;

/// <summary>
/// State machine for the MU login sequence.
/// Handles: Login → CharacterList → SelectCharacter → FocusCharacter → ClientReady
/// </summary>
public sealed class LoginFlow
{
    private readonly IConnection _connection;
    private readonly ClientVersion _clientVersion;
    private readonly bool _verbose;
    private readonly ILogger<LoginFlow> _logger;
    private readonly TaskCompletionSource<LoginResponse.LoginResult> _loginResponseTcs = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _characterListTcs = new();
    private readonly TaskCompletionSource<bool> _mapEnterTcs = new();

    private string? _selectedCharacterName;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginFlow"/> class.
    /// </summary>
    internal LoginFlow(
        IConnection connection,
        ClientVersion clientVersion,
        bool verbose,
        ILogger<LoginFlow>? logger = null)
    {
        this._connection = connection;
        this._clientVersion = clientVersion;
        this._verbose = verbose;
        this._logger = logger ?? NullLoggerFactory.Instance.CreateLogger<LoginFlow>();
    }

    /// <summary>
    /// Gets the name of the selected character, once login is complete.
    /// </summary>
    public string? SelectedCharacterName => this._selectedCharacterName;

    /// <summary>
    /// Gets whether this login flow is still waiting for responses.
    /// </summary>
    public bool IsActive => !this._loginResponseTcs.Task.IsCompleted
                              || !this._characterListTcs.Task.IsCompleted
                              || !this._mapEnterTcs.Task.IsCompleted;

    /// <summary>
    /// Executes the full login sequence.
    /// </summary>
    /// <param name="username">Account username.</param>
    /// <param name="password">Account password.</param>
    /// <param name="preferredCharacterName">Optional character name to select. If null, picks the first one.</param>
    /// <returns>True if login and character selection succeeded.</returns>
    internal async Task<bool> ExecuteAsync(string username, string password, string? preferredCharacterName)
    {
        try
        {
            // Step 1: Send login packet
            if (!await this.SendLoginAsync(username, password).ConfigureAwait(false))
            {
                return false;
            }

            // Step 2: Send character list request
            this._logger.LogInformation("Login OK, requesting character list...");
            await this._connection.SendRequestCharacterListAsync(0).ConfigureAwait(false);

            var characters = await this._characterListTcs.Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);

            if (characters.Count == 0)
            {
                this._logger.LogError("No characters available on this account.");
                return false;
            }

            // Step 3: Select character
            this._selectedCharacterName = preferredCharacterName ?? characters[0];
            if (!characters.Contains(this._selectedCharacterName))
            {
                this._logger.LogWarning(
                    "Character '{Name}' not found. Available: {List}. Using first available.",
                    this._selectedCharacterName,
                    string.Join(", ", characters));
                this._selectedCharacterName = characters[0];
            }

            this._logger.LogInformation("Selecting character '{Name}'...", this._selectedCharacterName);
            await this._connection.SendSelectCharacterAsync(this._selectedCharacterName).ConfigureAwait(false);

            // Wait for map enter (or other confirmation)
            await this._mapEnterTcs.Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);

            // Step 4: Focus character and send ready
            this._logger.LogInformation("Map entered, focusing character...");
            await this._connection.SendFocusCharacterAsync(this._selectedCharacterName).ConfigureAwait(false);
            await this._connection.SendClientReadyAfterMapChangeAsync().ConfigureAwait(false);

            this._logger.LogInformation("Character '{Name}' is now in game!", this._selectedCharacterName);
            return true;
        }
        catch (TimeoutException)
        {
            this._logger.LogError("Login sequence timed out. Check server and credentials.");
            return false;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Login sequence failed.");
            return false;
        }
    }

    /// <summary>
    /// Handles an incoming server packet during the login flow.
    /// Called by <see cref="TcpDebugClient.OnPacketReceived"/>.
    /// </summary>
    internal async ValueTask HandlePacketAsync(ReadOnlySequence<byte> packet)
    {
        // Copy packet to contiguous span for parsing
        var length = (int)packet.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        packet.CopyTo(rented);
        var span = rented.AsSpan(0, length);

        try
        {
            if (span.Length < 3)
            {
                return;
            }

            var header = span[0];
            var code = span[2];

            switch (code)
            {
                case 0xF1 when span.Length >= 4 && span[3] == 0x01:
                    // Login response: C1 0xF1 0x01 <result>
                    this.HandleLoginResponse(span);
                    break;

                case 0xF3 when span.Length >= 4 && span[3] == 0x00:
                    // Character list: C1/C2 0xF3 0x00 ...
                    this.HandleCharacterList(span);
                    break;

                case 0xF3 when span.Length >= 4 && span[3] == 0x03:
                    // Map enter / character select confirmation
                    this._mapEnterTcs.TrySetResult(true);
                    break;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private async Task<bool> SendLoginAsync(string username, string password)
    {
        // Build Xor3-encrypted credentials
        var xor3 = new Xor3Encryptor(0);

        // Username: 10 bytes, UTF-8, null-padded
        Span<byte> userBytes = stackalloc byte[10];
        var userLen = Encoding.UTF8.GetBytes(username, userBytes);
        userBytes[userLen..].Clear(); // null-pad
        xor3.Encrypt(userBytes);

        // Password: 20 bytes, UTF-8, null-padded
        Span<byte> passBytes = stackalloc byte[20];
        var passLen = Encoding.UTF8.GetBytes(password, passBytes);
        passBytes[passLen..].Clear(); // null-pad
        xor3.Encrypt(passBytes);

        // Build client version bytes (5 bytes: season LE, episode LE, language)
        Span<byte> versionBytes = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(versionBytes, (ushort)this._clientVersion.Season);
        BinaryPrimitives.WriteUInt16LittleEndian(versionBytes[2..], (ushort)this._clientVersion.Episode);
        versionBytes[4] = (byte)this._clientVersion.Language;

        // Client serial: 12 bytes of zeros (matches demo / test client behavior)
        Span<byte> serialBytes = stackalloc byte[12];
        serialBytes.Clear();

        // Send login (LongPassword variant)
        this._logger.LogInformation("Sending login for '{User}'...", username);
        await this._connection.SendLoginLongPasswordAsync(
            userBytes.ToArray(),
            passBytes.ToArray(),
            0, // tickCount
            versionBytes.ToArray(),
            serialBytes.ToArray()).ConfigureAwait(false);

        // Wait for login response
        var result = await this._loginResponseTcs.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        if (result != LoginResponse.LoginResult.Okay)
        {
            this._logger.LogError("Login failed: {Result}", result);
            return false;
        }

        return true;
    }

    private void HandleLoginResponse(Span<byte> span)
    {
        var response = new LoginResponseRef(span);
        var result = response.Success;
        this._logger.LogInformation("Login response: {Result}", result);
        this._loginResponseTcs.TrySetResult(result);
    }

    private void HandleCharacterList(Span<byte> span)
    {
        // Parse character names using the auto-generated CharacterListRef
        // Format: C1 0xF3 0x00 <UnlockFlags> <MoveCnt> <Count> <Flags> [CharacterDataRef...]
        // CharacterDataRef is 34 bytes each, name at offset 1, 10 bytes UTF-8
        CharacterListRef list = span;
        var count = list.CharacterCount;
        var names = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            var charData = list[i];
            var name = charData.Name.TrimEnd('\0');
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        this._logger.LogInformation(
            "Character list: {Count} chars - {Names}",
            names.Count,
            names.Count > 0 ? string.Join(", ", names) : "(none)");
        this._characterListTcs.TrySetResult(names.AsReadOnly());
    }
}
