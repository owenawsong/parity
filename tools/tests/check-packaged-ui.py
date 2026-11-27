"""Exercise the UI extracted from Parity Setup using a simulated Bluetooth host."""

import json
from pathlib import Path
import re
import sys
import time
from urllib.parse import unquote, urlsplit

from playwright.sync_api import expect, sync_playwright


ROOT = Path(__file__).resolve().parents[2]
ORIGIN = "https://app.pairside"
LOCALE = json.loads((ROOT / "src/WPods/locales/en-US.json").read_text(encoding="utf-8"))
MAIN_HOST = (ROOT / "ui/preview/host.js").read_text(encoding="utf-8")
POPUP_HOST = (ROOT / "ui/preview/popup-host.js").read_text(encoding="utf-8")
OFF = ("transparency", "off", "noiseCancellation")
ADAPTIVE = ("transparency", "adaptive", "noiseCancellation")
ADAPTIVE_OFF = ("transparency", "off", "adaptive", "noiseCancellation")
MODELS = (
    ("AirPods 1", "airpods", "classic-", ()),
    ("AirPods 2", "airpods", "classic-", ()),
    ("AirPods 3", "airpods3", "airpods3-", ()),
    ("AirPods 4", "airpods4", "airpods4-", ()),
    ("AirPods 4 (ANC)", "airpods4", "airpods4-", ADAPTIVE_OFF),
    ("AirPods 5", "airpods4", "airpods4-", ADAPTIVE_OFF),
    ("AirPods Pro 1", "pro", "pro1-", OFF),
    ("AirPods Pro 2", "pro", "pro2-", ADAPTIVE),
    ("AirPods Pro 3", "pro", "", ADAPTIVE),
    ("AirPods Max 1", "max", "max-", OFF),
    ("AirPods Max 2", "max", "max-", ADAPTIVE_OFF),
)


def open_page(browser, web, host, filename="index.html", width=520, reduced="reduce"):
    page = browser.new_page(viewport={"width": width, "height": 644}, reduced_motion=reduced)
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))

    def serve(route):
        path = web / unquote(urlsplit(route.request.url).path.lstrip("/"))
        if path.is_file() and path.is_relative_to(web):
            route.fulfill(path=path)
        else:
            errors.append(f"Missing packaged resource {path.name}")
            route.abort()

    page.route(f"{ORIGIN}/**", serve)
    page.add_init_script(script=f"const previewLocales = {json.dumps({'en-US': LOCALE})};\n{host}")
    page.goto(f"{ORIGIN}/{filename}")
    return page, errors


def expect_images(images, expected_files):
    expect(images).to_have_count(len(expected_files))
    for index, name in enumerate(expected_files):
        expect(images.nth(index)).to_have_attribute("src", re.compile(rf"(?:^|/){re.escape(name)}$"))
    images.evaluate_all("images => Promise.all(images.map(image => image.decode()))")


def overview(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        """const send = (data) => {
          if (data.type === 'init') window.initialSnapshot = structuredClone(data);
          receive?.({data: structuredClone(data)});
        }; window.fixture = send;""",
    )
    page, errors = open_page(browser, web, host)
    page.wait_for_function("window.initialSnapshot?.state")
    initial = page.evaluate("initialSnapshot")

    def send(snapshot):
        page.evaluate("snapshot => window.fixture(snapshot)", snapshot)

    send({**initial, "devices": [], "state": None})
    expect(page.get_by_role("heading", name="No AirPods yet")).to_be_visible()
    expect(page.get_by_role("button", name="Open Bluetooth settings")).to_be_visible()
    base = initial["devices"][0]
    send({**initial, "devices": [{**base, "conn": "notConnected"}], "state": None, "attached": None, "desktopOnly": True})
    expect(page.get_by_text("Connect through Windows Bluetooth.", exact=False)).to_be_visible()
    expect(page.get_by_role("button", name="Open Bluetooth settings", exact=True)).to_be_visible()
    expect(page.get_by_role("button", name="Connect", exact=True)).to_be_hidden()
    for model, art, prefix, modes in MODELS:
        parts = ("headphone",) if art == "max" else ("left", "right", "case")
        batteries = dict.fromkeys(("left", "right", "case", "headphone"))
        batteries.update({part: {"pct": 70 + i, "charging": False} for i, part in enumerate(parts)})
        caps = (["listeningMode"] if modes else []) + (["adaptive"] if "adaptive" in modes else [])
        caps += (["singleBattery"] if art == "max" else []) + (["allowOff"] if modes == ADAPTIVE else [])
        if art != "max":
            caps.append("micSide")
        tracking = model in ("AirPods Pro 3", "AirPods Max 2")
        if tracking:
            caps.append("sleepDetection")
        device = {**base, "name": model, "model": model, "art": art, "caps": caps, "batteries": batteries,
                  "conn": "connected", "stereoMicSupported": model not in ("AirPods Pro 1", "AirPods Max 1"),
                  "saved": {**base["saved"], "allowOff": False, "listeningMode": "transparency"}}
        state = {**initial["state"], "caps": caps, "listeningMode": "transparency", "allowOff": False,
                 "ready": True, "checking": False, "battery": batteries}
        send({**initial, "devices": [device], "state": state})
        hero = page.locator(".wp-hero--controls")
        expect(hero).to_be_visible()
        expect_images(hero.locator("img.wp-battery__art:visible"), [f"{prefix}{part}.webp" for part in parts])
        labels = page.locator(".wp-home__listening:visible label:visible input[name=nc]")
        actual = labels.evaluate_all("fields => fields.map(field => field.value)")
        assert tuple(actual) == modes, (model, actual, modes)
        if art != "max":
            case = hero.locator('[data-part="case"]:visible')
            expect(case).to_be_visible()
            expect(case.locator(".wp-battery__pct")).to_have_text("72%")
        if modes == ADAPTIVE:
            send({"type": "settings", "settings": {"allowOff": True}})
            expect(page.locator("label:visible:has(input[name=nc][value=off])")).to_be_visible()
        if model in ("AirPods Pro 1", "AirPods Pro 3", "AirPods Max 1"):
            check_model_pages(page, tracking=tracking, mic_side=art != "max")
        send({**initial, "devices": [{**device, "conn": "notConnected"}], "state": None, "attached": None})
        offline = page.locator(".wp-hero--offline")
        expect(offline.get_by_role("button", name="Connect", exact=True)).to_be_visible()
        expect(page.locator(".wp-recovery")).to_be_hidden()
        if art == "max":
            expect_images(offline.locator(".wp-offline-poster img:visible"), ["max-headphone.webp"])
        else:
            expect_images(offline.locator(".wp-offline-buds img:visible"), [f"{prefix}left.webp", f"{prefix}right.webp"])
        expect(page.locator(".wp-overview-dashboard")).to_be_hidden()
        expect(page.locator(".wp-activity")).to_be_hidden()
    assert not errors, errors
    page.close()


def check_model_pages(page, *, tracking, mic_side):
    page.locator('[data-destination="audioMotion"]').click()
    room = page.locator(".wp-room__body")
    expect(room).to_be_visible()
    mode = room.get_by_role("radio", name="Head tracked")
    if tracking:
        expect(mode).to_be_enabled()
        expect(room.get_by_role("status")).to_contain_text("Fixed or Head tracked")
    else:
        expect(mode).to_be_hidden()
        expect(room.get_by_role("status")).to_contain_text("Choose Fixed to spatialize")
    page.locator('[data-destination="micSetup"]').click()
    expect(page.get_by_role("heading", name="Microphone", exact=True)).to_be_visible()
    options = page.locator(".wp-mic-options")
    if mic_side:
        expect(options.get_by_role("combobox", name="Microphone side")).to_be_visible()
    else:
        expect(options).to_be_hidden()
    page.locator('[data-destination="home"]').click()


def nearby(browser, web):
    page, errors = open_page(browser, web, POPUP_HOST, "popup.html", width=330, reduced="no-preference")
    connect = page.get_by_role("button", name="Connect", exact=True)
    for model, art, prefix, _ in MODELS:
        page.evaluate("message => window.postMessage(message, '*')",
                      {"latchwavePopup": True, "model": model, "art": art, "simulate": True})
        expect(connect).to_be_visible()
        expect_images(page.locator(".wp-popup__artwork img:visible"),
                      [f"{prefix}{part}.webp" for part in (("headphone",) if art == "max" else ("left", "right", "case"))])
        connect.click()
        expect(page.locator('.wp-popup__offer[data-state="connecting"]')).to_be_visible()
        expect(page.get_by_role("button", name="Cancel", exact=True)).to_be_visible()
        expect(page.locator('.wp-popup__offer[data-state="connected"]')).to_be_visible()
        page.get_by_role("button", name="Close", exact=True).click()
        expect(connect).to_be_hidden()
    assert not errors, errors
    page.close()


def overview_connection(browser, web):
    page, errors = open_page(browser, web, MAIN_HOST, reduced="no-preference")

    def scenario(name):
        page.evaluate("scenario => window.postMessage({pairsidePreview: true, scenario}, '*')", name)

    scenario("nearby")
    poster = page.locator(".wp-hero__visual--still .wp-hero__art--closed:visible")
    expect_images(poster, ["case.webp"])
    expect(poster).to_have_css("animation-name", "wp-hero-art-arrive")
    page.locator(".wp-home__device-controls").evaluate("""async controls => {
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        await Promise.allSettled(controls.getAnimations().map(animation => animation.finished));
    }""")
    stage = page.locator(".wp-hero__visual")
    nearby_box = stage.bounding_box()
    page.get_by_role("button", name="Connect", exact=True).click()
    expect(page.get_by_role("button", name="Cancel", exact=True)).to_be_visible()
    expect(poster).to_be_visible()
    connecting_box = stage.bounding_box()
    assert abs(nearby_box["x"] - connecting_box["x"]) <= 2, (nearby_box, connecting_box)
    assert abs(nearby_box["y"] - connecting_box["y"]) <= 12, (nearby_box, connecting_box)
    settled = page.locator(".wp-hero--controls img.wp-battery__art:visible")
    expect_images(settled, ["left.webp", "right.webp", "case.webp"])
    page.get_by_role("button", name="Disconnect", exact=True).click()
    expect(page.get_by_role("button", name="Connect", exact=True)).to_be_visible()
    scenario("nearby")
    expect(poster).to_be_visible()
    page.get_by_role("button", name="Connect", exact=True).click()
    expect_images(settled, ["left.webp", "right.webp", "case.webp"])
    assert not errors, errors
    page.close()


def overview_registration(browser, web):
    for width in (400, 460, 520, 560, 900):
        page, errors = open_page(browser, web, MAIN_HOST, width=width)
        nav = page.locator(".wp-destinations")
        expect(nav).to_have_css("flex-direction", "row" if width < 460 else "column")
        pane = page.locator(".wp-body").bounding_box()
        anchors = []
        action_edges = []
        for scenario, action, stage in (
            ("pro", "Disconnect", "controls"), ("nearby", "Connect", "discovery"),
            ("disconnected", "Connect", "offline"), ("failed", "Retry controls", "offline"),
        ):
            page.evaluate("scenario => window.postMessage({pairsidePreview: true, scenario}, '*')", scenario)
            hero = page.locator(f".wp-hero--{stage}")
            button = hero.get_by_role("button", name=action, exact=True)
            expect(button).to_be_visible()
            page.evaluate("""async () => {
                await document.fonts.ready;
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            }""")
            title = hero.locator(".wp-devswitch__name:visible").bounding_box()
            anchors.append((title["x"], title["y"]))
            area = button.bounding_box()
            assert 0 <= area["x"] and area["x"] + area["width"] <= width, (scenario, width, area)
            action_edges.append((area["x"] + area["width"], area["y"]))
            if scenario == "pro":
                hardware = hero.locator(".wp-hero__visual").bounding_box()
                listening = page.locator(".wp-home__listening").bounding_box()
                assert listening["y"] >= hardware["y"] + hardware["height"], (width, hardware, listening)
                marker = page.locator(".wp-mode-balance__track")
                for pseudo, pattern in (("::before", "dotted"), ("::after", "solid")):
                    shape = marker.evaluate("""(track, pseudo) => {
                        const style = getComputedStyle(track, pseudo);
                        return [style.width, style.height, style.borderTopStyle, style.borderTopLeftRadius];
                    }""", pseudo)
                    assert shape == ["13px", "13px", pattern, "50%"], (width, shape)
                if width == 560:
                    slider = page.locator(".wp-mode-balance__input")
                    before = int(slider.input_value())
                    slider.press("ArrowRight")
                    expect(slider).to_have_value(str(before + 1))
                player = page.locator(".wp-now-playing")
                pause = player.get_by_role("button", name="Pause", exact=True)
                expect(pause).to_be_enabled()
                pause.click(trial=True)
                expect(player.get_by_text("Low Tide", exact=True)).to_be_visible()
            if scenario == "nearby":
                visual = hero.locator(".wp-hero__visual").bounding_box()
                charge = hero.locator(".wp-nearby-levels").bounding_box()
                assert charge["y"] >= visual["y"] + visual["height"] + 12, (width, visual, charge)
            if scenario == "failed":
                recovery = page.locator(".wp-recovery")
                expect(recovery.get_by_role("heading", name="AirPods controls couldn’t connect.")).to_be_visible()
                expect(recovery.locator("details").first).not_to_have_attribute("open", "")
                art = hero.locator(".wp-hero__offline-art").bounding_box()
                support = recovery.bounding_box()
                assert support["y"] >= art["y"] + art["height"], (width, art, support)
            if scenario in ("nearby", "disconnected"):
                artwork = hero.locator(".wp-hero__visual" if scenario == "nearby" else ".wp-hero__offline-art").bounding_box()
                assert abs(artwork["x"] + artwork["width"] / 2 - (pane["x"] + pane["width"] / 2)) <= 2, (width, scenario, artwork)
                assert abs(artwork["y"] + artwork["height"] / 2 - (pane["y"] + pane["height"] / 2)) <= 30, (width, scenario, artwork)
        assert all(abs(x - anchors[0][0]) <= 1 and abs(y - anchors[0][1]) <= 1 for x, y in anchors), (width, anchors)
        assert all(abs(x - action_edges[0][0]) <= 2 and abs(y - action_edges[0][1]) <= 2
                   for x, y in action_edges), (width, action_edges)
        assert not errors, errors
        page.close()


def onboard_equalizer(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        """const send = (data) => {
          if (data.type === 'init') window.initialSnapshot = structuredClone(data);
          receive?.({data: structuredClone(data)});
        }; window.fixture = send; window.eqCommands = [];""",
    ).replace(
        "postMessage: (command) => queueMicrotask(() => onCommand(command)),",
        """postMessage: command => {
          if (command.type === 'setOnboardEq' || command.type === 'previewOnboardEq') {
            window.eqCommands.push(command);
          } else queueMicrotask(() => onCommand(command));
        },""",
    )
    page, errors = open_page(browser, web, host)
    page.locator('[data-destination="audioMotion"]').click()
    eq = page.locator(".wp-eq-card")
    bass = eq.get_by_role("slider", name="Bass")
    expect(bass).to_be_enabled()
    initial = page.evaluate("initialSnapshot.state")
    page.clock.install(time=0)
    page.clock.pause_at(1_000)

    def send(**message):
        page.evaluate("message => window.fixture(message)", message)

    def edit(value):
        bass.evaluate("""(slider, value) => {
          slider.valueAsNumber = value;
          slider.dispatchEvent(new Event('input', {bubbles: true}));
        }""", value)

    def settle():
        bass.dispatch_event("change")
        page.clock.run_for(250)

    def commands():
        return page.evaluate("eqCommands")

    for value in (58, 62, 67):
        edit(value)
    page.clock.run_for(120)
    assert not commands()
    page.clock.run_for(60)
    previews = commands()
    assert [(c["type"], c["value"]["low"]) for c in previews] == [("previewOnboardEq", 67)], previews
    settle()
    preview, commit = commands()
    assert commit == {**preview, "type": "setOnboardEq"}
    assert commit["connectionId"] == initial["connectionId"]
    send(type="onboardEqResult", id=commit["id"], request=commit["request"], status="sent")
    page.clock.run_for(32)
    expect(eq.get_by_text("Sent to your AirPods", exact=True)).to_be_visible()
    send(type="settings", id=commit["id"], settings={"onboardEq": commit["value"]})
    page.clock.run_for(32)
    expect(eq.get_by_text("Saved on your AirPods", exact=True)).to_be_visible()

    edit(79)
    page.clock.run_for(180)
    edit(67)
    settle()
    commit = commands()[-1]
    send(type="onboardEqResult", id=commit["id"], request=commit["request"], status="sent")
    page.clock.run_for(32)
    expect(eq.get_by_text("Sent to your AirPods", exact=True)).to_be_visible()

    edit(83)
    bass.dispatch_event("change")
    count = len(commands())
    new_connection = initial["connectionId"] + 1
    send(type="state", state={**initial, "connectionId": new_connection,
                             "onboardEq": {**initial["onboardEq"], "low": 43}})
    send(type="onboardEqResult", id=commit["id"], request=commit["request"], status="failed")
    page.clock.run_for(500)
    assert len(commands()) == count, commands()
    expect(bass).to_have_value("43")
    expect(eq.get_by_text("Read from AirPods", exact=True)).to_be_visible()

    edit(54)
    settle()
    commit = commands()[-1]
    assert commit["connectionId"] == new_connection and commit["value"]["low"] == 54
    send(type="onboardEqResult", id=commit["id"], request=commit["request"], status="failed")
    page.clock.run_for(32)
    retry = eq.get_by_role("button", name="Retry save", exact=True)
    expect(retry).to_be_visible()
    retry.dispatch_event("click")
    retried = commands()[-1]
    assert retried == {**commit, "request": commit["request"] + 1}
    assert not errors, errors
    page.close()


def audio_workspaces(browser, web):
    for width in (400, 520):
        page, errors = open_page(browser, web, MAIN_HOST, width=width)
        navigation = page.locator(".wp-destinations")
        expect(navigation).to_be_visible()
        assert navigation.evaluate("nav => nav.scrollWidth <= nav.clientWidth"), width

        def navigate(route):
            destination = page.locator(f'[data-destination="{route}"]')
            glyph = ":is(.wp-destination__label, .wp-destination__short):visible" if width < 460 else "svg"
            expect(destination.locator(glyph)).to_be_visible()
            destination.focus()
            destination.press("Enter")
            expect(destination).to_have_attribute("aria-current", "page")

        navigate("audioMotion")
        eq = page.locator(".wp-eq-card")
        expect(eq).to_be_visible()
        expect(eq.locator(".wp-sound-band__caption")).to_have_count(3)
        expect(eq.get_by_role("slider", name="Bass")).to_be_enabled()
        room = page.locator(".wp-spatial-room")
        expect(room.locator(".wp-room__output-caption")).to_have_text("AirPods Pro")
        modes = room.get_by_role("radio", name="Head tracked")
        expect(modes).to_be_visible()
        control = room.locator(".wp-room__mode-select").bounding_box()
        assert control["y"] + control["height"] <= 610, (width, control)

        navigate("micSetup")
        card = page.locator(".wp-mic-audition")
        record = card.get_by_role("button", name="Record sample", exact=True)
        expect(record).to_be_enabled()
        page.clock.install()
        idle_size = card.bounding_box()["height"]
        record.click()
        stop = card.get_by_role("button", name="Stop recording")
        expect(stop).to_be_enabled()
        expect(stop).to_have_class(re.compile(r"\bwp-btn--secondary\b"))
        assert abs(card.bounding_box()["height"] - idle_size) <= 1, width
        page.clock.fast_forward(1400)
        stop.click()
        expect(card.get_by_role("button", name="Play sample", exact=True)).to_be_enabled()
        expect(card.get_by_role("button", name="Record again", exact=True)).to_be_enabled()
        expect(card.locator(".wp-mic-sample-summary")).to_have_text("Sample · 0:01")
        assert abs(card.bounding_box()["height"] - idle_size) <= 1, width
        assert card.evaluate("card => card.scrollWidth === card.clientWidth"), width
        assert not errors, errors
        page.close()


def motion_feedback(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        "const send = (data) => receive?.({ data: structuredClone(data) }); window.fixture = send;",
    ).replace(
        "postMessage: (command) => queueMicrotask(() => onCommand(command)),",
        """postMessage: command => {
          (window.commands ||= []).push(command);
          if (!['startHeadMotion', 'stopHeadMotion', 'setMicSamplePlayback'].includes(command.type))
            queueMicrotask(() => onCommand(command));
        },""",
    )
    page, errors = open_page(browser, web, host)

    def send(**message):
        page.evaluate("message => window.fixture(message)", message)

    page.locator('[data-destination="controls"]').click()
    motions = page.locator(".wp-gesture-remote")
    toggle = motions.get_by_role("switch", name="Gesture shortcuts")
    toggle.click()
    assert page.evaluate("commands.findLast(c => c.type === 'startHeadMotion').name") == "playback"
    gesture = dict(type="headMotion", id="pro", status="ready", samples=20, gesture=None,
                   playback=True, count=0, pitch=0, yaw=0)
    send(**gesture)
    practice = motions.get_by_role("button", name="Practice", exact=True)
    practice.click()
    assert page.evaluate("commands.findLast(c => c.type === 'startHeadMotion').name ?? null") is None
    send(**{**gesture, "practicing": True, "pitch": 16})
    pad = motions.locator(".wp-motion-card__dot")
    expect(pad).to_be_visible()
    expect(pad).to_have_css("transform", "matrix(1, 0, 0, 1, 0, -16)")
    send(**{**gesture, "practicing": True, "pitch": -14})
    expect(pad).to_have_css("transform", "matrix(1, 0, 0, 1, 0, 14)")
    motions.get_by_role("button", name="Stop practice", exact=True).click()
    send(**gesture)
    expect(toggle).to_be_checked()
    expect(practice).to_be_enabled()
    expect(pad).to_be_hidden()

    page.locator('[data-destination="audioMotion"]').click()
    expect(page.get_by_role("heading", name="Sound", exact=True)).to_be_visible()
    room = page.locator(".wp-room__field")
    room.scroll_into_view_if_needed()
    expect(room).to_be_in_viewport()
    page.clock.install()
    send(type="spotifySpatial", id="pro", source="windowsMix", mode="tracked", status="playing")
    page.clock.run_for(40)
    send(type="spotifyPose", id="pro", yaw=179, pitch=0)
    listener = page.locator(".wp-room-listener")
    page.clock.run_for(32)
    expect(listener).to_have_attribute("style", re.compile(r"rotate\(179deg\)"))
    page.clock.run_for(195)
    send(type="spotifyPose", id="pro", yaw=-179, pitch=0)
    page.clock.run_for(36)
    expect(listener).to_have_attribute("style", re.compile(r"rotate\(181deg\)"))
    assert int(listener.evaluate("el => el.style.getPropertyValue('--room-pose-duration')").removesuffix("ms")) >= 140

    page.set_viewport_size({"width": 520, "height": 270})
    page.locator(".wp-body").evaluate("el => el.scrollTop = 0")
    expect(room).not_to_be_in_viewport()
    page.clock.run_for(80)
    send(type="spotifyPose", id="pro", yaw=-155, pitch=0)
    page.clock.run_for(64)
    expect(listener).to_have_attribute("style", re.compile(r"rotate\(181deg\)"))
    page.set_viewport_size({"width": 520, "height": 644})
    room.scroll_into_view_if_needed()
    expect(room).to_be_in_viewport()
    expect(listener).to_have_attribute("style", re.compile(r"rotate\(205deg\)"))

    page.locator('[data-destination="micSetup"]').click()
    page.set_viewport_size({"width": 520, "height": 400})
    expect(page.get_by_role("heading", name="Microphone", exact=True)).to_be_visible()
    expect(page.locator(".wp-scroll--more")).to_be_visible()
    page.locator(".wp-body").evaluate("el => el.scrollTop = el.scrollHeight")
    page.clock.run_for(60)
    expect(page.locator(".wp-scroll--more")).to_have_count(0)
    page.get_by_role("button", name="Record sample", exact=True).click()
    record = page.evaluate("commands.findLast(c => c.type === 'recordMicCheck')")
    send(type="micCheck", request=record["request"], status="ready", endpoint=record["id"],
         sample=True, durationMs=1000, envelope=[.15, .33, .21])
    page.get_by_role("button", name="Play sample", exact=True).click()
    start = page.evaluate("commands.findLast(c => c.type === 'setMicSamplePlayback')")
    send(type="micSamplePlayback", request=record["request"], revision=start["value"],
         positionMs=80, playing=True, failed=False)
    playhead = page.locator(".wp-sample-playhead")
    page.clock.run_for(50)
    first_position = playhead.evaluate("el => el.style.transform")
    page.clock.run_for(50)
    second_position = playhead.evaluate("el => el.style.transform")
    assert first_position != second_position, (first_position, second_position)
    page.get_by_role("button", name="Pause sample", exact=True).click()
    paused = playhead.evaluate("el => el.style.transform")
    page.clock.run_for(170)
    assert playhead.evaluate("el => el.style.transform") == paused
    assert not errors, errors
    page.close()


def pc_equalizer(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        "const send = (data) => receive?.({ data: structuredClone(data) }); window.emitPcEqFixture = send;",
    ).replace(
        "postMessage: (command) => queueMicrotask(() => onCommand(command)),",
        "postMessage: command => { (window.eqCommands ||= []).push(command); queueMicrotask(() => onCommand(command)); },",
    )
    page, errors = open_page(browser, web, host, width=520)
    page.locator('[data-destination="audioMotion"]').click()
    panel = page.locator(".wp-pceq")
    expect(panel).to_have_count(0)
    assert not page.evaluate("eqCommands.some(c => c.type === 'readPcEq')")

    def open_advanced():
        page.locator('[data-destination="windows"]').click()
        page.get_by_role("button", name="Advanced settings", exact=False).click()
        expect(panel.locator("summary")).to_be_visible()
        expect(panel).not_to_have_attribute("open", "")

    def open_eq():
        open_advanced()
        panel.locator("summary").click()

    open_advanced()
    assert not page.evaluate("eqCommands.some(c => c.type === 'readPcEq')")
    panel.locator("summary").click()
    expect(panel.get_by_role("slider")).to_have_count(10)
    page.wait_for_function("() => window.eqCommands?.some(c => c.type === 'readPcEq')")
    pair = page.evaluate("eqCommands.find(c => c.type === 'readPcEq').id")
    curve = [0] * 10
    profile = {"type": "pcEq", "id": pair, "enabled": False, "flat": False, "bands": curve, "presets": []}

    def emit(message):
        page.evaluate("value => window.emitPcEqFixture(value)", message)

    emit(profile)
    toggle = panel.get_by_role("button", name="Turn on")
    expect(toggle).to_be_enabled()
    toggle.click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'setPcEqEnabled').value") is True
    profile["enabled"] = True
    emit(profile)
    route = {"type": "spotifySpatial", "source": "windowsMix", "id": pair, "mode": "stereo",
             "status": "waiting", "routed": True, "pcEqEnabled": True, "pcEqPlaying": False}
    emit(route)
    expect(panel.get_by_text("Waiting for audio through the selected playback source.")).to_be_visible()
    expect(panel.locator(".wp-pceq-summary")).to_have_text("On")
    route.update(status="playing", pcEqPlaying=True)
    emit(route)
    expect(panel.locator(".wp-pceq-summary")).to_have_text("Active")

    knob = panel.get_by_role("slider", name="1000 Hz", exact=True)
    knob.evaluate("input => { input.value = 4; input.dispatchEvent(new Event('input')); }")
    page.wait_for_function("() => eqCommands.some(c => c.type === 'previewPcEq' && c.value[5] === 4)")
    knob.evaluate("input => input.dispatchEvent(new Event('change'))")
    command = page.evaluate("eqCommands.findLast(c => c.type === 'savePcEq')")
    assert command["value"] == [0, 0, 0, 0, 0, 4, 0, 0, 0, 0]
    emit({**profile, "bands": command["value"], "request": command["request"]})
    expect(knob).to_have_value("4")
    panel.get_by_role("button", name="Listen flat").click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'comparePcEq').value") is True
    emit({**profile, "bands": command["value"], "flat": True})
    expect(panel.get_by_text("Playing flat for comparison. The curve is saved.")).to_be_visible()
    expect(knob).to_have_value("4")
    panel.get_by_role("button", name="Save preset").click()
    panel.get_by_role("textbox", name="Preset name").fill("Vocal lift")
    panel.get_by_role("button", name="Save", exact=True).click()
    saved = page.evaluate("eqCommands.findLast(c => c.type === 'savePcEqPreset')")
    assert saved["name"] == "Vocal lift" and saved["value"] == command["value"]
    emit({**profile, "bands": saved["value"], "request": saved["request"],
          "presets": [{"name": saved["name"], "bands": saved["value"]}]})
    expect(panel.locator("select option").last).to_have_text("Vocal lift")
    panel.get_by_role("button", name="Turn off", exact=True).click()
    emit({**profile, "bands": saved["value"], "enabled": False})
    emit({**route, "pcEqEnabled": False, "pcEqPlaying": False})
    expect(panel.locator(".wp-pceq-summary")).to_have_text("Off")
    direct = panel.get_by_role("button", name="Return to direct playback")
    expect(direct).to_be_visible()
    expect(panel.get_by_role("button", name="Turn on", exact=True)).to_be_enabled()
    direct.click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'returnToDirectPlayback').id") == pair
    emit({**route, "routed": False, "status": "idle", "pcEqEnabled": False, "pcEqPlaying": False})
    expect(direct).to_be_hidden()

    audio_routing = page.locator(".wp-room__tools").filter(has_text="Audio routing")
    panel.get_by_role("button", name="Change playback source").click()
    expect(audio_routing).to_have_attribute("open", "")
    page.wait_for_function("() => eqCommands.findLast(c => c.type === 'watchSpotifyRoom')?.value === true")
    picker = audio_routing.locator("select")
    remember = audio_routing.get_by_role("switch", name="Remember sound settings")
    apps = [{"key": "spotify", "name": "Spotify"}, {"key": "appleMusic", "name": "Apple Music"}]
    source = {**route, "source": "spotify", "status": "idle", "routed": False, "profileId": pair,
              "apps": apps, "rememberedSources": []}
    emit(source)
    expect(picker).to_have_value("spotify")
    expect(remember).not_to_be_checked()
    remember.click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'rememberAppSound')") == {
        "type": "rememberAppSound", "id": pair, "name": "spotify", "value": True}
    emit({**source, "rememberedSources": ["spotify"]})
    emit({**profile, "scope": "spotify"})
    expect(remember).to_be_checked()
    expect(picker.locator('option[value="spotify"]')).to_have_text("Spotify · Saved")
    open_eq()
    expect(panel.get_by_text("Editing the saved curve for Spotify")).to_be_visible()

    panel.get_by_role("button", name="Import or share a curve").click()
    share_code = panel.get_by_role("textbox", name="Parity curve code")
    expect(share_code).to_have_value("parity-eq:v1:0,0,0,0,0,0,0,0,0,0")
    share_code.fill("parity-eq:v1:99,0,0,0,0,0,0,0,0,0")
    panel.get_by_role("button", name="Apply code").click()
    expect(panel.get_by_text("Paste a Parity curve code", exact=False)).to_be_visible()
    expect(knob).to_have_value("0")
    share_code.fill("parity-eq:v1:0,0,0,0,0,-4,0,0,0,0")
    panel.get_by_role("button", name="Apply code").click()
    expect(knob).to_have_value("-4")
    imported = page.evaluate("eqCommands.findLast(c => c.type === 'savePcEq')")
    assert imported["code"] == "spotify" and imported["value"][5] == -4
    assert page.evaluate("eqCommands.findLast(c => c.type === 'setPcEqEnabled').value") is False
    emit({**profile, "scope": "spotify", "bands": imported["value"], "request": imported["request"]})

    knob.evaluate("input => { input.value = 1; input.dispatchEvent(new Event('input')); }")
    panel.get_by_role("button", name="Change playback source").click()
    expect(audio_routing).to_have_attribute("open", "")
    last_save = page.evaluate("eqCommands.findLast(c => c.type === 'savePcEq')")
    assert last_save["code"] == "spotify" and last_save["value"][5] == 1
    picker.select_option("appleMusic")
    assert page.evaluate("eqCommands.findLast(c => c.type === 'setSpatialPlayer')") == {
        "type": "setSpatialPlayer", "id": pair, "name": "appleMusic"}
    emit({**source, "source": "appleMusic", "rememberedSources": ["spotify"]})
    emit({**profile, "enabled": False})
    expect(remember).not_to_be_checked()
    open_eq()
    expect(panel.get_by_text("Editing the saved curve for Spotify")).to_be_hidden()
    expect(knob).to_have_value("0")
    panel.get_by_role("button", name="Change playback source").click()
    expect(audio_routing).to_have_attribute("open", "")
    picker.select_option("spotify")
    emit({**source, "rememberedSources": ["spotify"]})
    emit({**profile, "scope": "spotify", "bands": last_save["value"]})
    expect(remember).to_be_checked()
    open_eq()
    expect(knob).to_have_value("1")
    panel.get_by_role("button", name="Change playback source").click()
    expect(audio_routing).to_have_attribute("open", "")
    emit({**source, "id": None, "rememberedSources": ["spotify"], "savedMode": "fixed", "savedEqualizer": True})
    recall = audio_routing.get_by_role("button", name="Apply saved settings")
    expect(recall).to_be_enabled()
    recall.click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'recallAppSound')") == {
        "type": "recallAppSound", "id": pair, "name": "spotify"}
    emit({**source, "rememberedSources": ["spotify"], "savedMode": "fixed", "savedEqualizer": True,
          "mode": "fixed", "status": "waiting", "routed": True, "pcEqEnabled": True})
    expect(recall).to_be_hidden()
    remember.click()
    assert page.evaluate("eqCommands.findLast(c => c.type === 'rememberAppSound').value") is False
    emit(source)
    emit(profile)
    expect(remember).not_to_be_checked()
    audio_routing.locator("summary").click()
    page.wait_for_function("() => eqCommands.findLast(c => c.type === 'watchSpotifyRoom')?.value === false")
    open_eq()
    expect(knob).to_have_value("0")
    assert not errors, errors
    page.close()


def docked_batteries(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        """const send = (data) => {
          if (data.type === 'init') window.initialSnapshot = structuredClone(data);
          receive?.({data: structuredClone(data)});
        }; window.fixture = send;""",
    )
    page, errors = open_page(browser, web, host, width=430)
    page.wait_for_function("window.initialSnapshot?.state")
    page.clock.install()
    pair = page.evaluate("initialSnapshot.devices[0]")
    seen_at = page.evaluate("Date.now()")
    case_report = {"pct": 81, "charging": False, "source": "case", "seenAt": seen_at}
    left_bud = {"pct": 94, "charging": True}
    right_bud = {"pct": 93, "charging": True}

    def report(left, right):
        device = {**pair, "conn": "notConnected", "caseBroadcast": case_report,
                  "caseBuds": {"left": left, "right": right, "seenAt": seen_at}}
        page.evaluate("message => window.fixture(message)",
                      {"type": "devices", "devices": [device], "attached": None})

    report(left_bud, right_bud)
    levels = page.locator(".wp-nearby-levels")
    expect(levels.get_by_text("94%", exact=True)).to_be_visible()
    expect(levels.get_by_text("93%", exact=True)).to_be_visible()
    expect(levels.get_by_text("81%", exact=True)).to_be_visible()
    expect(page.get_by_role("button", name="Disconnect", exact=True)).to_have_count(0)
    report(left_bud, None)
    expect(levels.get_by_text("93%", exact=True)).to_be_hidden()
    expect(levels.get_by_text("94%", exact=True)).to_be_visible()
    page.clock.fast_forward(46_000)
    expect(levels).to_be_hidden()
    assert not errors, errors
    page.close()


def device_information(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        """const send = (data) => {
          if (data.type === 'init') window.initialSnapshot = structuredClone(data);
          receive?.({data: structuredClone(data)});
        }; window.fixture = send;""",
    )
    page, errors = open_page(browser, web, host, width=430)
    page.wait_for_function("window.initialSnapshot?.state")
    initial = page.evaluate("initialSnapshot")
    pair = {**initial["devices"][0], "modelNumber": "A3063",
            "firmwareBuild": "91.3431046004000000.29", "firmwareLive": True}

    def devices(device, attached):
        page.evaluate("message => window.fixture(message)",
                      {"type": "devices", "devices": [device], "attached": attached})

    devices(pair, pair["id"])
    page.get_by_role("button", name="Settings", exact=True).click()
    info = page.locator(".wp-device-information")
    expect(info).to_be_visible()
    expect(info.get_by_text("A3063", exact=True)).to_be_hidden()
    info.locator("summary").press("Enter")
    expect(info.get_by_text("A3063", exact=True)).to_be_visible()
    expect(info.get_by_text("Firmware build", exact=True)).to_be_visible()
    expect(info.get_by_text(pair["firmwareBuild"], exact=True)).to_be_visible()
    devices({**pair, "conn": "disconnected", "firmwareLive": False}, None)
    expect(info.get_by_text("Last reported firmware", exact=True)).to_be_visible()
    expect(info.get_by_text(pair["firmwareBuild"], exact=True)).to_be_visible()
    second = {**initial["devices"][1], "modelNumber": "A2096", "conn": "disconnected"}
    devices(second, None)
    expect(info).not_to_have_attribute("open", "")
    info.locator("summary").press("Enter")
    expect(info.get_by_text("A2096", exact=True)).to_be_visible()
    expect(info.get_by_text("Connect to check", exact=True)).to_be_visible()
    expect(info.get_by_text(pair["firmwareBuild"], exact=True)).to_have_count(0)
    assert not errors, errors
    page.close()


def device_switch(browser, web):
    host = MAIN_HOST.replace(
        "const send = (data) => receive?.({ data: structuredClone(data) });",
        """const send = (data) => {
          if (data.type === 'init') window.initialSnapshot = structuredClone(data);
          if (data.type === 'micSetup') window.lastMicSetup = structuredClone(data);
          receive?.({data: structuredClone(data)});
        }; window.fixture = send;""",
    ).replace(
        "postMessage: (command) => queueMicrotask(() => onCommand(command)),",
        """postMessage: command => {
          (window.commands ||= []).push(command);
          if (command.type === 'setListeningMode' || (window.deferMic && command.type === 'watchMicSetup') ||
              (window.deferConnectionCheck && command.type === 'readConnectionCheck') ||
              (window.deferConnect && ['connect', 'cancel', 'disconnect'].includes(command.type))) return;
          queueMicrotask(() => onCommand(command));
        },""",
    )
    page, errors = open_page(browser, web, host)
    page.wait_for_function("window.initialSnapshot?.state")
    initial = page.evaluate("initialSnapshot")
    pro = initial["devices"][0]
    base_state = initial["state"]
    max_caps = ["listeningMode", "singleBattery"]
    max_pair = {**initial["devices"][1], "name": "Max test pair", "model": "AirPods Max 1", "conn": "connected",
                "caps": max_caps, "stereoMicSupported": False}

    def send(**message):
        page.evaluate("message => window.fixture(message)", message)

    def attach(device, state):
        send(type="devices", devices=[device], attached=device["id"])
        send(type="state", state=state)

    # Mode reports from an earlier click or a different set of earbuds cannot move the control.
    page.locator('label:has(input[name=nc][value=transparency])').click()
    page.locator('label:has(input[name=nc][value=noiseCancellation])').click()
    requests = page.evaluate("commands.filter(command => command.type === 'setListeningMode')")
    assert len(requests) == 2, requests
    send(type="listeningMode", id=pro["id"], request=requests[0]["request"], listeningMode="transparency")
    expect(page.locator('input[name=nc][value=noiseCancellation]')).to_be_checked()
    send(type="listeningMode", id=pro["id"], request=requests[1]["request"], listeningMode="noiseCancellation")
    send(type="listeningMode", id=max_pair["id"], listeningMode="transparency")
    expect(page.locator('input[name=nc][value=noiseCancellation]')).to_be_checked()
    send(type="settings", id=max_pair["id"], settings={"allowOff": True})
    expect(page.locator('label:has(input[name=nc][value=off])')).to_be_hidden()

    # An already-linked second pair is selectable. The Cancel button stays a
    # cancel even after the control channel opens but before it becomes ready.
    page.evaluate("window.deferConnect = true")
    send(type="devices", devices=[pro, {**max_pair, "conn": "available"}], attached=pro["id"])
    page.locator("button.wp-devswitch").click()
    page.get_by_role("menuitemradio", name="Max test pair Available on this PC").click()
    page.get_by_role("button", name="Open controls", exact=True).click()
    assert page.evaluate("commands.findLast(c => c.type === 'connect').id") == max_pair["id"]
    send(type="devices", devices=[pro, {**max_pair, "conn": "connecting"}], attached=pro["id"])
    cancel = page.get_by_role("button", name="Cancel", exact=True)
    expect(cancel).to_be_visible()
    send(type="devices", devices=[{**pro, "conn": "available"}, max_pair], attached=max_pair["id"])
    send(type="state", state={**base_state, "caps": max_caps, "ready": False, "checking": True})
    cancel.click()
    assert page.evaluate("commands.findLast(c => c.type === 'cancel').id") == max_pair["id"]
    assert not page.evaluate("commands.some(c => c.type === 'disconnect')")
    attach(pro, base_state)

    page.keyboard.press("Control+k")
    search = page.locator(".wp-find input")
    search.fill("voice enhancemen")
    expect(page.locator(".wp-find__option").first).to_contain_text("Microphone")
    page.locator(".wp-find__option").first.evaluate("row => window.firstSearchRow = row")
    search.fill("voice enhancement")
    assert page.locator(".wp-find__option").first.evaluate("row => row === window.firstSearchRow")
    attach(max_pair, {**base_state, "caps": max_caps, "listeningMode": "transparency"})
    expect(search).to_have_value("voice enhancement")
    expect(page.locator(".wp-find__option")).to_have_count(0)
    search.press("Escape")

    attach(pro, base_state)
    page.locator('[data-destination="micSetup"]').click()
    record = page.get_by_role("button", name="Record sample", exact=True)
    expect(record).to_be_enabled()
    old_route = page.evaluate("lastMicSetup")
    page.evaluate("window.deferMic = true")
    attach(max_pair, {**base_state, "caps": max_caps, "listeningMode": "transparency"})
    page.wait_for_function("old => commands.some(c => c.type === 'watchMicSetup' && c.value && c.request > old)",
                           arg=old_route["watchRequest"])
    expect(record).to_be_disabled()
    send(**old_route)
    expect(record).to_be_disabled()
    expect(page.locator(".wp-mic-source__name > span")).to_have_text("Max test pair")
    new_request = page.evaluate("commands.findLast(c => c.type === 'watchMicSetup' && c.value).request")
    send(**{**old_route, "watchRequest": new_request, "targetId": "max-mic", "target": "Max test pair"})
    expect(record).to_be_enabled()

    page.locator('[data-destination="home"]').click()
    send(type="devices", devices=[{**pro, "conn": "attachFailed"}, max_pair], attached=max_pair["id"])
    page.locator("button.wp-devswitch").click()
    page.get_by_role("menuitemradio", name=re.compile(re.escape(pro["name"]))).click()
    failed = page.locator(".wp-hero--offline")
    expect(failed.get_by_role("status")).to_have_text("AirPods controls couldn’t connect.")
    expect_images(failed.locator(".wp-offline-buds img:visible"), ["left.webp", "right.webp"])
    failed.get_by_role("button", name="Retry controls").click()
    assert page.evaluate("commands.findLast(c => c.type === 'connect').id") == pro["id"]
    assert not page.evaluate("commands.some(c => c.type === 'disconnect' || c.type === 'reconnect')")
    expect(page.locator(".wp-overview-dashboard")).to_be_hidden()
    expect(page.locator(".wp-activity")).to_be_hidden()

    page.evaluate("window.deferConnectionCheck = true")
    recovery = page.locator(".wp-recovery")
    assert not page.evaluate("commands.some(c => c.type === 'readConnectionCheck')")
    recovery.get_by_text("Troubleshooting", exact=True).click()
    check = recovery.locator(".wp-connection-check")
    expect(check.get_by_role("button", name="Checking…")).to_be_disabled()
    request = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck')")
    assert request["id"] == pro["id"], request
    send(type="connectionCheck", request=request["request"], service="running", serviceVersion="",
         connected=False, knownEndpoints=True, playback="Speakers", callInput=None, callOutput=None)
    expect(check.get_by_role("heading", name="AirPods controls are disconnected")).to_be_visible()
    check.get_by_text("Connection details", exact=True).click()
    expect(check.locator("dd").nth(1)).to_have_text("Disconnected")
    expect(check.get_by_role("button", name="Check again")).to_be_enabled()
    page.clock.install()
    check.get_by_role("button", name="Check again").click()
    page.clock.fast_forward(5200)
    expect(check.get_by_text("The check took too long. Try again.")).to_be_visible()
    expect(check.get_by_text("Speakers", exact=True)).to_be_visible()
    expect(check.locator(".wp-check-refresh time")).to_contain_text("Last checked")
    expect(check.get_by_role("button", name="Check again")).to_be_enabled()
    check.get_by_role("button", name="Check again").click()
    newest = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck')")
    now = int(time.time() * 1000)
    result = dict(type="connectionCheck", request=newest["request"], service="running", serviceVersion="",
                  connected=False, knownEndpoints=True, playback="Windows speakers", callInput=None, callOutput=None,
                  events=[
                      {"kind": "motionMissing", "time": now - 1000, "ageSeconds": 1, "count": 3},
                      {"kind": "controlsReady", "time": now - 3000, "ageSeconds": 3, "count": 1},
                  ])
    send(**result)
    send(**{**result, "request": request["request"], "connected": True, "playback": "Previous output"})
    expect(check.locator("dd").nth(1)).to_have_text("Disconnected")
    expect(check.get_by_text("Windows speakers", exact=True)).to_be_visible()
    expect(check.get_by_text("The check took too long. Try again.")).to_be_hidden()
    recent = check.locator(".wp-connection-events")
    recent.locator("summary").click()
    expect(recent.locator("li").first).to_contain_text("No motion data arrived · 3 times")
    expect(recent.locator("time")).to_have_count(2)
    recovery.get_by_role("button", name="Connection report").click()
    report = page.locator(".wp-report")
    page.wait_for_function("id => commands.findLast(c => c.type === 'readConnectionCheck')?.request > id", arg=newest["request"])
    report_request = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck').request")
    send(**{**result, "request": report_request})
    expect(report).to_have_attribute("open", "")
    expect(report.locator("textarea")).to_have_value(re.compile(r"1s before check · No motion data arrived · 3 times"))
    assert pro["name"] not in report.locator("textarea").input_value()
    advanced = page.locator(".wp-advanced-page .wp-connection-check")
    textarea = report.locator("textarea")
    saved_report = textarea.input_value()
    advanced.get_by_role("button", name="Check again").click()
    expect(textarea).to_have_value(saved_report)
    page.clock.fast_forward(5200)
    expect(advanced.get_by_text("The check took too long. Try again.")).to_be_visible()
    expect(textarea).to_have_value(saved_report)

    def checked(**overrides):
        advanced.get_by_role("button", name="Check again").click()
        seq = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck').request")
        send(**{**result, "request": seq, "connected": True, "events": [], **overrides})

    # Presence and friendly names are separate. A support report reveals neither
    # the headset name nor the names of someone's Windows audio endpoints.
    checked(playback=None, callInput=None, callMuted=True, callOutput="Private office headset",
            endpoints={"playback": True, "callInput": True, "callOutput": True})
    expect(advanced.get_by_role("heading", name="The default call microphone is muted")).to_be_visible()
    expect(advanced.get_by_text("Unnamed audio device · muted", exact=True)).to_be_visible()
    expect(textarea).to_have_value(re.compile(r"Default call input: muted\nDefault call output: available"))
    assert "Private office headset" not in textarea.input_value()
    page.evaluate("""Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
        writeText: async text => { window.copiedReport = text; }
    } })""")
    report.get_by_role("button", name="Copy report").click()
    expect(report.get_by_role("status")).to_have_text("Copied")
    assert page.evaluate("window.copiedReport") == textarea.input_value()
    page.evaluate("() => { navigator.clipboard.writeText = async () => { throw Error('Unavailable'); }; }")
    report.get_by_role("button", name="Copy report").click()
    expect(textarea).to_be_focused()
    expect(report.get_by_role("status")).to_have_text("Press Ctrl+C to copy.")
    assert textarea.evaluate("e => e.selectionStart === 0 && e.selectionEnd === e.value.length")

    checked(playback=None, endpoints={"playback": False, "callInput": False, "callOutput": False})
    expect(advanced.get_by_role("heading", name="Default playback device unavailable")).to_be_visible()
    advanced.get_by_role("button", name="Open Sound settings").click()
    assert page.evaluate("commands.at(-1).type") == "openSoundSettings"
    checked(playback=None, knownEndpoints=False)
    expect(advanced.get_by_text("Parity couldn’t identify the default Windows playback device.")).to_be_visible()
    checked(playback=None, knownEndpoints=True)  # An older host could omit the name of an active output.
    expect(textarea).to_have_value(re.compile(r"Default playback device: unknown"))
    checked(endpoints={"playback": True, "callInput": False, "callOutput": False})
    expect(advanced.get_by_role("heading", name="AirPods controls are connected")).to_be_visible()
    expect(advanced.get_by_text("Windows playback is set to Windows speakers.")).to_be_visible()
    checked(virtualDefaultOutsideParity=True)
    expect(advanced.get_by_role("heading", name="Windows is using virtual speakers")).to_be_visible()
    checked(connected=False, service="appOnly")
    advanced.get_by_role("button", name="Open Bluetooth settings").click()
    assert page.evaluate("commands.at(-1).type") == "openBluetoothSettings"

    previous = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck').request")
    attach(max_pair, {**base_state, "caps": max_caps})
    expect(advanced.get_by_role("heading", name="Checking the connection…")).to_be_visible()
    changed_pair = page.evaluate("commands.findLast(c => c.type === 'readConnectionCheck')")
    assert changed_pair["id"] == max_pair["id"] and changed_pair["request"] > previous
    send(**{**result, "request": previous})
    expect(textarea).to_have_value(re.compile(r"no completed check"))
    expect(advanced.locator(".wp-check-refresh time")).to_be_hidden()
    page.clock.fast_forward(5200)
    expect(advanced.get_by_role("heading", name="No check result yet")).to_be_visible()
    send(**{**result, "request": changed_pair["request"], "connected": True, "playback": "Second pair output"})
    expect(advanced.get_by_text("Windows playback is set to Second pair output.")).to_be_visible()
    send(type="devices", devices=[{**pro, "conn": "attachFailed"}], attached=None)
    expect(advanced.get_by_role("heading", name="Checking the connection…")).to_be_visible()
    page.locator(".wp-content-back").click()
    troubleshoot = recovery.locator(".wp-connection-disclosure > summary")
    expect(troubleshoot).to_be_focused()
    expect(recovery.locator("details").first).not_to_have_attribute("open", "")
    troubleshoot.press("Enter")
    expect(check.get_by_role("button", name="Checking…")).to_be_disabled()
    attach(pro, base_state)
    expect(recovery).to_be_hidden()
    expect(page.get_by_role("button", name="Disconnect", exact=True)).to_be_visible()
    # The title can receive focus while the new connection is still initializing.
    expect(page.locator(".wp-hero :is(.wp-devswitch, .wp-hero__actions button):focus")).to_have_count(1)
    assert not errors, errors
    page.close()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Pass the desktop directory extracted by Parity Setup --verify-package.")
    stage = Path(sys.argv[1]).resolve()
    if not (stage / "parity-installed.json").is_file():
        raise SystemExit("Expected a desktop directory extracted by Setup.")
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(channel="msedge", args=["--mute-audio"])
        overview(browser, stage / "web")
        nearby(browser, stage / "web")
        overview_connection(browser, stage / "web")
        overview_registration(browser, stage / "web")
        onboard_equalizer(browser, stage / "web")
        audio_workspaces(browser, stage / "web")
        motion_feedback(browser, stage / "web")
        pc_equalizer(browser, stage / "web")
        docked_batteries(browser, stage / "web")
        device_information(browser, stage / "web")
        device_switch(browser, stage / "web")
    print(f"Packaged UI passed first run, offline recovery, battery artwork, listening modes, and Nearby for {len(MODELS)} model profiles.")
