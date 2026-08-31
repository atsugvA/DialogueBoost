#!/usr/bin/env python3
"""Report how Jellyfin exposes and selects the audio tracks of a library's items.

For every item in the named library this prints the audio streams as the server
reports them, which one it picks as the default, and whether an explicit request
for each stream is honoured — checked against the transcode URL the server hands
back for a client profile that cannot direct-play.

This is the measurement half of the default-track spike; scripts/make-audio-fixtures.sh
builds the library it reads.

    JELLYFIN_USER=… JELLYFIN_PASS=… ./scripts/probe-audio-selection.py [library]

Credentials come from the environment, never from this repo. JELLYFIN_URL
defaults to http://localhost:8096.
"""

import json
import os
import sys
import urllib.request

URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096").rstrip("/")
USER = os.environ.get("JELLYFIN_USER")
PASS = os.environ.get("JELLYFIN_PASS", "")
LIBRARY = sys.argv[1] if len(sys.argv) > 1 else "DBM1"

# A profile with no direct-play entries, so the server must commit to a transcode
# and name the audio stream index it decided on.
FORCE_TRANSCODE = {
    "MaxStreamingBitrate": 140000000,
    "DirectPlayProfiles": [], "ContainerProfiles": [], "CodecProfiles": [], "SubtitleProfiles": [],
    "TranscodingProfiles": [{
        "Container": "ts", "Type": "Video", "AudioCodec": "aac", "VideoCodec": "h264",
        "Context": "Streaming", "Protocol": "hls", "MaxAudioChannels": "2",
        "MinSegments": 1, "BreakOnNonKeyFrames": True,
    }],
}


def call(path, body=None, headers=None):
    req = urllib.request.Request(
        URL + path,
        headers=headers or HEADERS,
        data=json.dumps(body).encode() if body is not None else None,
        method="POST" if body is not None else "GET",
    )
    return json.load(urllib.request.urlopen(req, timeout=30))


if not USER:
    sys.exit("set JELLYFIN_USER (and JELLYFIN_PASS) — credentials are never stored in this repo")

auth = call(
    "/Users/AuthenticateByName",
    {"Username": USER, "Pw": PASS},
    {"Content-Type": "application/json",
     "Authorization": 'MediaBrowser Client="DBDev", Device="cli", DeviceId="db-probe", Version="1.0.0"'},
)
TOKEN, UID = auth["AccessToken"], auth["User"]["Id"]
HEADERS = {"Authorization": f"MediaBrowser Token={TOKEN}", "Content-Type": "application/json"}

libs = [i for i in call(f"/Items?userId={UID}&Recursive=false")["Items"] if i["Name"] == LIBRARY]
if not libs:
    sys.exit(f"no library named {LIBRARY!r} on {URL}")
items = call(f"/Items?userId={UID}&ParentId={libs[0]['Id']}&Recursive=true"
             f"&IncludeItemTypes=Movie,Episode&Fields=Path")["Items"]

for item in sorted(items, key=lambda i: i["Name"]):
    source = call(f"/Items/{item['Id']}?userId={UID}&Fields=MediaStreams,MediaSources")["MediaSources"][0]
    audio = [s for s in source["MediaStreams"] if s["Type"] == "Audio"]
    default_index = call(f"/Items/{item['Id']}/PlaybackInfo?userId={UID}",
                         {"UserId": UID, "MaxStreamingBitrate": 140000000},
                         )["MediaSources"][0].get("DefaultAudioStreamIndex")

    print("=" * 88)
    print(item["Name"])
    for stream in audio:
        asked = call(f"/Items/{item['Id']}/PlaybackInfo?userId={UID}",
                     {"UserId": UID, "MaxStreamingBitrate": 140000000, "MediaSourceId": source["Id"],
                      "AudioStreamIndex": stream["Index"], "DeviceProfile": FORCE_TRANSCODE,
                      "StartTimeTicks": 0})["MediaSources"][0]
        url = asked.get("TranscodingUrl") or ""
        got = url.split("AudioStreamIndex=")[1].split("&")[0] if "AudioStreamIndex=" in url else None
        print("   idx=%-2d %-9s default=%-5s external=%-5s selectable=%-5s  %s"
              % (stream["Index"], stream.get("Codec"), stream["IsDefault"], bool(stream.get("IsExternal")),
                 got == str(stream["Index"]), stream.get("DisplayTitle")))
    print(f"   → DefaultAudioStreamIndex = {default_index}")
