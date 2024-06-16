using OpenVoiceSharp;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

public class VoiceChatManager : MonoBehaviour
{
    // openvoicesharp
    public VoiceChatInterface VoiceChatInterface = new(stereo: true, enableNoiseSuppression: false); 
    public BasicMicrophoneRecorder MicrophoneRecorder = new(stereo: true);
    public int BufferSamples = VoiceUtilities.GetSampleSize(2) / 2;

    // steam
    public Lobby? Lobby;
    public bool Connected = false;

    // audio playback
    private Dictionary<SteamId, CircularAudioBuffer<float>> VoiceBuffers = new();
    [SerializeField]
    public bool AllowLoopback = false;

    // those are assigned in the editor directly
    public Canvas Canvas;
    public Button InteractButton;
    public GameObject StatusLabel;
    public GameObject ConnetedPlayersLabel;
    public GameObject LobbyListContent;

    public GameObject ProfileExample;

    public Toggle NoiseSuppresionBox;
    public Toggle ReverbBox;

    public AudioMixer AudioMixer;

    // avoid some repetitive actions
    public TextMeshProUGUI GetTextMeshPro(GameObject label) => label.GetComponent<TextMeshProUGUI>();
    public void SetText(GameObject label, string text) => GetTextMeshPro(label).text = text;
    public void SetStatus(string text) => SetText(StatusLabel, text);
    private void UpdatePlayerCountLabel() => SetText(ConnetedPlayersLabel, $"Players in lobby ({Lobby?.MemberCount ?? 0})");

    private T GetComponentFromProfile<T>(GameObject profile, string name) where T : Component
        => profile.transform.Find(name).gameObject.GetComponent<T>();

    private GameObject? GetProfile(SteamId steamId) => LobbyListContent.transform.Find(steamId.ToString())?.gameObject ?? null;
    private async Task CreateProfile(Friend friend)
    {
        // avoid creating clones
        if (GetProfile(friend.Id) != null) return;

        // Debug.Log($"Creating profile for {friend.Id.ToSafeString()}");
        Steamworks.Data.Image avatarImage = (Steamworks.Data.Image)await friend.GetMediumAvatarAsync();

        // create clone with name steam id
        GameObject profile = Instantiate(ProfileExample, LobbyListContent.transform);
        profile.name = friend.Id.ToString();

        // load avatar
        Texture2D texture = new((int)avatarImage.Width, (int)avatarImage.Height, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Trilinear
        };

        // flip image (required for unity)
        for (int x = 0; x < avatarImage.Width; x++)
        {
            for (int y = 0; y < avatarImage.Height; y++)
            {
                var pixel = avatarImage.GetPixel(x, y); 
                texture.SetPixel(x, (int)avatarImage.Height - y, new UnityEngine.Color(pixel.r / 255.0f, pixel.g / 255.0f, pixel.b / 255.0f, pixel.a / 255.0f));
            }
        }
        texture.Apply();

        var pictureComponent = GetComponentFromProfile<RawImage>(profile, "Picture");
        pictureComponent.texture = texture;

        // load name
        var nameComponent = GetComponentFromProfile<TextMeshProUGUI>(profile, "Username");
        nameComponent.text = friend.Name;

        // create audio clip
        AudioSource voiceSource = GetComponentFromProfile<AudioSource>(profile, "VoiceSource");

        // allows us to push voice data & read it when needed
        CircularAudioBuffer<float> buffer = new(BufferSamples, RecommendedChunkAmount.Unity);

        VoiceBuffers[friend.Id] = buffer;

        AudioClip audioClip = AudioClip.Create(
            "Voice",
            buffer.BufferLength, // RECOMMENDED BUFFER AMOUNT 
            2, 
            48000, 
            false
            // i do not use pcm read callback as it requires 4096 samples
            // bad for latency and memory usage
        );

        voiceSource.clip = audioClip;
    }
    public void DeleteProfile(SteamId steamId)
    {
        VoiceBuffers.Remove(steamId);
        Destroy(GetProfile(steamId));
    }

    public async Task SetupLobby()
    {
        // create all previous profiles
        foreach (var member in Lobby?.Members)
            await CreateProfile(member);

        UpdatePlayerCountLabel();
    }

    public void ToggleReverb(bool enabled)
        // god this sucks so much FUCK you unity
        => AudioMixer.SetFloat("Reverb", enabled ? 0 : -10000);

    void Start()
    {
        VoiceBuffers.Clear();
        UpdatePlayerCountLabel();

        // host/leave button
        InteractButton.onClick.AddListener(async() =>
        {
            // leave/stop hosting
            if (Lobby != null)
            {
                // clear player list and audio playbacks
                foreach (var member in Lobby?.Members)
                {
                    // delete all previous profiles
                    foreach (Transform child in LobbyListContent.transform)
                        Destroy(child.gameObject);
                }

                Lobby?.Leave();
                Lobby = null;
                Connected = false;

                SetStatus("Not connected");
                UpdatePlayerCountLabel();

                InteractButton.GetComponentInChildren<TextMeshProUGUI>().text = "Host";

                return;
            }

            SetStatus("Creating lobby...");
            var createLobbyOutput = await SteamMatchmaking.CreateLobbyAsync(4);
            if (createLobbyOutput == null)
            {
                SetStatus("Not connected");
                return;
            }

            Lobby = createLobbyOutput.Value;

            Lobby?.SetPublic();
            Lobby?.SetJoinable(true);

            SetStatus("Joining lobby...");
            await Lobby?.Join();
        });

        // noise suppression toggle
        NoiseSuppresionBox.onValueChanged.AddListener((enabled) =>
        {
            VoiceChatInterface.EnableNoiseSuppression = enabled;
        });

        // reverb toggle
        ReverbBox.onValueChanged.AddListener(ToggleReverb);
        ToggleReverb(false); // disable on startup

        // steam events
        SteamMatchmaking.OnLobbyMemberJoined += async (lobby, friend) =>
        {
            await CreateProfile(friend);

            UpdatePlayerCountLabel();
        };
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, friend) =>
        {
            UpdatePlayerCountLabel();

            DeleteProfile(friend.Id);
        };

        SteamMatchmaking.OnLobbyEntered += async (joinedLobby) =>
        {
            SetStatus("Connected");

            // set to current lobby
            Lobby = joinedLobby;

            // setup
            await SetupLobby();

            InteractButton.GetComponentInChildren<TextMeshProUGUI>().text = "Stop hosting";

            Connected = true;
        };
        SteamFriends.OnGameLobbyJoinRequested += async (joinedLobby, friend) =>
        {
            // do not accept any join requests if already in lobby
            if (Lobby != null) return;

            SetStatus("Accepted invite, joining...");

            // set to current lobby
            Lobby = joinedLobby;

            // setup
            await joinedLobby.Join();

            InteractButton.GetComponentInChildren<TextMeshProUGUI>().text = "Leave";
            Connected = true;
        };

        // microphone rec
        MicrophoneRecorder.DataAvailable += (pcmData, length) => {
            // if not connected or not talking, ignore
            if (!Connected) return;
            if (!VoiceChatInterface.IsSpeaking(pcmData)) return;

            // encode the audio data and apply noise suppression.
            (byte[] encodedData, int encodedLength) = VoiceChatInterface.SubmitAudioData(pcmData, length);

            // send packet to everyone (P2P)
            foreach (SteamId steamId in VoiceBuffers.Keys)
                SteamNetworking.SendP2PPacket(steamId, encodedData, encodedLength, 0, P2PSend.Reliable);
        };
        MicrophoneRecorder.StartRecording();

        // initialize steam
        SteamClient.Init(480);
    }

    void HandleMessageFrom(SteamId steamid, byte[] data)
    {
        if (!AllowLoopback && steamid == SteamClient.SteamId) return;

        if (!VoiceBuffers.ContainsKey(steamid))
            return;

        // decode and convert to float32
        (byte[] decodedData, int decodedLength) = VoiceChatInterface.WhenDataReceived(data, data.Length);

        float[] samples = new float[decodedLength / 2];

        VoiceUtilities.Convert16BitToFloat(decodedData, samples);

        // push to circular voice buffer
        var circularBuffer = VoiceBuffers[steamid];
        circularBuffer.PushChunk(samples);

        VoiceBuffers[steamid] = circularBuffer;
    }

    void Update()
    {
        SteamClient.RunCallbacks();

        while (SteamNetworking.IsP2PPacketAvailable())
        {
            var packet = SteamNetworking.ReadP2PPacket();
            if (!packet.HasValue) continue;
            
            HandleMessageFrom(packet.Value.SteamId, packet.Value.Data);
        }

        if (Lobby == null) return;

        var keys = VoiceBuffers.Keys.ToArray();

        // handle voice buffers reading (I recommend you thread this!!)
        SteamId steamId;
        for (int i = 0; i < keys.Length; i++)
        {
            steamId = keys[i];

            // create all previous profiles
            if (!VoiceBuffers.ContainsKey(steamId)) continue;

            // check if full
            CircularAudioBuffer<float> voiceBuffer = VoiceBuffers[steamId];
            if (!voiceBuffer.BufferFull) continue;

            // submit to audio clip and play
            AudioSource voiceSource = GetComponentFromProfile<AudioSource>(GetProfile(steamId), "VoiceSource");

            // read all data and play
            voiceSource.clip.SetData(voiceBuffer.ReadAllBuffer(), 0);
            voiceSource.Play();

            VoiceBuffers[steamId] = voiceBuffer;
        }
    }
}
