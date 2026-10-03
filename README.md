# SoulsProx

Proximity voice chat for **Seamless Co-op** in Dark Souls III. Dark Souls Remastered, Dark Souls II: SotFS and Elden Ring are coming next.

You hear your friend at full volume when you're close and quieter as you move apart, then not at all past a set distance. Their voice also comes from the side they're on: if they're to your right, you hear them in your right ear.

SoulsProx is a small app you run next to the game. It **only reads** the game's memory to see where your characters are. It never changes the game, so it can't break your save or clash with Seamless Co-op. Your voice goes straight from your PC to your friend's, encrypted. It doesn't pass through any server.

## Setup (both players)

1. Download `SoulsProx.exe` and put it anywhere, e.g. your Desktop. You don't need to install anything.
2. Run it.
   - If Windows says *"Windows protected your PC"*, click **More info → Run anyway**. This happens because the app isn't code-signed.
   - If Windows Firewall asks, click **Allow**. Voice can't get through otherwise.
3. Click the **⚙ gear** (top left) and pick your **Microphone** and **Speaker** (headphones). Press ← or Esc to go back.
4. Click the **+ circle** (*Connect a friend*). Click **Copy code** and send the code to your friend on Discord.
5. Paste your friend's code under **Friend's code** and click **Connect**.
6. When your friend's circle shows their name, you're connected. You both have to paste each other's codes.

Your code normally stays the same between sessions, and the app reconnects to your friend automatically next time. If you can't hear each other, swap codes again. Your internet address may have changed.

Then start the game the usual way (Seamless Co-op launcher) and play. SoulsProx finds the game on its own.

## Tips

- **Use headphones.** Echo cancellation (on by default, in ⚙ Settings) removes your friend's voice from your mic if it leaks out of your headphones or speakers, so they don't hear themselves. Headphones still sound best.
- **Leave the Discord voice call** (or mute it) while using SoulsProx. Each app can only cancel the echo of its own audio, so with both running your mics pick up the other app and you hear yourselves twice. Also turn off the game's own voice chat.
- **Voice activity** is on by default. Watch the mic bar at the bottom of the window. In ⚙ Settings, move *Sensitivity* (the yellow line) so the bar only turns green when you talk. You can also switch to **Push to talk** and pick a key, mouse button or controller button.
- **Radio key:** hold it to be heard at full volume wherever you are. It works like a walkie-talkie for when you split up.
- When your friend talks, their circle gets a green ring (purple when they use the radio). The bar under their name shows how loud they are right now.
- **Full volume within / Silent beyond** (in ⚙ Settings) set the distances. The defaults are 5 m and 35 m.
- **Far-away volume** above 0% means you can always faintly hear each other.
- In menus and loading screens you talk normally, at full volume.
- **Test beacon:** you can test alone. Load into the game, open ⚙ Settings → Advanced → *Test beacon*, click **Drop here** and talk. You'll hear your own voice, delayed, coming from that spot. Walk away and turn the camera to check the fading and left/right.
- If left and right sound backwards, tick **Swap left/right**.

## If you can't connect

- Make sure you **both** pasted each other's codes and clicked Connect.
- Some routers and VPNs block direct connections. If one of you uses a VPN, turn it off, or switch its NAT setting to "moderate" if it has one.
- **Fallback with Tailscale** (free):
  1. Both install [Tailscale](https://tailscale.com) and join the same tailnet.
  2. Click the friend circle to open *Connect*. Under **If codes don't connect**, enter the friend's Tailscale IP followed by `:47800`, e.g. `100.101.102.103:47800`.
  3. Click **Connect** again. You still need each other's codes; the address only changes where packets are sent.
- **Fallback with port forwarding:** one of you forwards **UDP port 47800** on their router to their PC. The other enters `their-public-ip:47800` under *If codes don't connect*.
- In ⚙ Settings → Advanced, click **Open log folder** and send `log.txt` to whoever is helping you.

## For developers

```
dotnet build                                   # everything
dotnet test                                    # unit + localhost network tests
dotnet run --project src/SoulsProx             # the app
dotnet run --project src/SoulsProx.Probe       # print what's read from the running game
dotnet run --project src/SoulsProx.Probe -- --selftest --seconds 10   # mic/speakers/STUN check
dotnet publish src/SoulsProx -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

To try two copies on one PC, run `SoulsProx.exe --profile a` and `SoulsProx.exe --profile b --port 47801`, then swap their codes.

Layout:
- `src/SoulsProx.Core`: game memory reading (`Games/`, `Memory/`), proximity math (`Proximity/`), audio: WASAPI via NAudio, Opus via Concentus, WebRTC echo cancellation (`Audio/`), and the encrypted UDP link with STUN hole punching (`Net/`).
- `src/SoulsProx`: the WPF window, styled after CrewLink (`App.xaml` theme, `MainWindow.xaml`, `MainViewModel.cs`).
- `src/SoulsProx.Probe`: console diagnostics.

## Credits

Game memory layouts come from the community's reverse-engineering work:
- [SoulMemory / SoulSplitter](https://github.com/FrankvdStam/SoulSplitter) by FrankvdStam (DS3 WorldChrMan signature)
- [DS3RuntimeScripting](https://github.com/AmySouls/DS3RuntimeScripting) by AmySouls (DS3 player, position, camera and player-slot offsets)
- [The Grand Archives](https://github.com/The-Grand-Archives) Cheat Engine tables

Audio: [NAudio](https://github.com/naudio/NAudio) (MIT), [Concentus](https://github.com/lostromb/concentus) (BSD), a C# port of the Opus codec, and the [WebRTC audio processing module](https://webrtc.googlesource.com/src/+/refs/heads/main/modules/audio_processing/) (BSD) for echo cancellation and noise suppression, via [SoundFlow](https://github.com/LSXPrime/SoundFlow)'s build (MIT). License texts: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
