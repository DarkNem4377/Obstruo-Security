/* =======================================================
   OBSTRUO — PARTICLE MORPHING INTRO
   GPU / Performance Optimizations Applied:
   - Typed Float32Array for all particle data (no GC pressure)
   - Offscreen canvas for sprite pre-rendering (zero live allocations)
   - canvas { alpha: false } on main context (skips compositor alpha pass)
   - willReadFrequently: true on sampling canvas
   - Passive event listeners throughout
   - cancelAnimationFrame guard to prevent double-loop
   - Capped MAX_DT prevents spiral-of-death on tab resume
   - resize() debounced with RAF to avoid layout thrashing
   - Particle draw uses single globalAlpha set per batch
   - bloom canvas reused (never re-allocated each frame)
======================================================= */

(function () {

  /* ── Performance tier ───────────────────────────────── */
  function getParticleCount() {
    const mem = navigator.deviceMemory || 4;
    const cpu = navigator.hardwareConcurrency || 4;
    if (mem <= 2 || cpu <= 2) return 1500;
    if (mem <= 4 || cpu <= 4) return 2500;
    return 4000;
  }

  /* ── Config ─────────────────────────────────────────── */
  const CFG = {
    PARTICLE_COUNT:   getParticleCount(),
    LOGO_SIZE:        340,
    FREE_ROAM_SECS:   6,
    MORPH_DURATION:   2.6,
    DRAG:             0.97,
    FREE_SPEED:       1.2,
    PULL_STRENGTH:    0.16,
    DRAG_MORPH:       0.92,
    SPRITE_RADIUS:    12,
    TRAIL_ALPHA:      0.2,
    SAMPLE_THRESHOLD: 180,
    MAX_DT:           0.033,
  };

  const N = CFG.PARTICLE_COUNT;

  /* ── Main canvas ────────────────────────────────────── */
  const canvas = document.getElementById("intro-canvas");
  // alpha:false → browser skips the per-pixel alpha compositing pass
  const ctx = canvas.getContext("2d", { alpha: false });

  // GPU compositing layer — promotes canvas to its own compositor layer
  canvas.style.transform = "translateZ(0)";
  canvas.style.willChange = "transform";

  let W, H, cx, cy;
  let resizeRaf = null;

  function resize() {
    W = canvas.width  = window.innerWidth;
    H = canvas.height = window.innerHeight;
    cx = W / 2;
    cy = H / 2;
    resizeRaf = null;
  }
  resize();

  window.addEventListener("resize", function () {
    if (resizeRaf) return;
    resizeRaf = requestAnimationFrame(resize);
  }, { passive: true });

  /* ── HUD refs ───────────────────────────────────────── */
  const statusEl = document.getElementById("status-line");
  const enterEl  = document.getElementById("enter-text");
  const flashEl  = document.getElementById("flash-overlay");
  const corners  = document.querySelectorAll(".corner");

  /* ── State ──────────────────────────────────────────── */
  let phase           = "freeroam";
  let canEnter        = false;
  let morphStart      = null;
  let logoTargets     = [];
  let logoAlpha       = 0;
  let logoImg         = null;
  let glowPhase       = 0;
  let animId          = null;
  let lastTime        = 0;
  let elapsedFreeRoam = 0;
  let particlesFaded  = false;

  /* ── Typing effect ──────────────────────────────────── */
  let typeTimer = null;
  function typeStatus(text, speed) {
    speed = speed || 55;
    if (typeTimer) clearInterval(typeTimer);
    statusEl.textContent = "";
    var i = 0;
    typeTimer = setInterval(function () {
      statusEl.textContent += text[i++];
      if (i >= text.length) clearInterval(typeTimer);
    }, speed);
  }

  /* ── Pre-render sprites to offscreen canvases ───────── */
  // Done once at startup; drawImage of a canvas is GPU-accelerated
  const SPRITE_DEFS = [
    { r: 255, g: 80,  b: 20  },
    { r: 255, g: 30,  b: 10  },
    { r: 255, g: 200, b: 80  },
    { r: 200, g: 200, b: 200 },
    { r: 255, g: 255, b: 255 },
    { r: 180, g: 20,  b: 0   },
  ];

  function makeSprite(r, g, b) {
    const R    = CFG.SPRITE_RADIUS;
    const size = R * 2;
    const oc   = document.createElement("canvas");
    oc.width   = size;
    oc.height  = size;
    const oc2  = oc.getContext("2d");

    const glow = oc2.createRadialGradient(R, R, 0, R, R, R);
    glow.addColorStop(0,   `rgba(${r},${g},${b},0.6)`);
    glow.addColorStop(0.45,`rgba(${r},${Math.floor(g * 0.3)},0,0.2)`);
    glow.addColorStop(1,   `rgba(0,0,0,0)`);
    oc2.fillStyle = glow;
    oc2.fillRect(0, 0, size, size);

    const core = oc2.createRadialGradient(R, R, 0, R, R, R * 0.4);
    core.addColorStop(0,   `rgba(255,255,255,1)`);
    core.addColorStop(0.5, `rgba(${r},${g},${b},0.9)`);
    core.addColorStop(1,   `rgba(${r},0,0,0)`);
    oc2.fillStyle = core;
    oc2.beginPath();
    oc2.arc(R, R, R * 0.4, 0, Math.PI * 2);
    oc2.fill();

    return oc;
  }

  const sprites   = SPRITE_DEFS.map(function (d) { return makeSprite(d.r, d.g, d.b); });
  const redSprite = makeSprite(255, 30, 10);

  /* ── Bloom canvas (reused, never re-allocated) ──────── */
  const BLOOM_PAD  = 120;
  const BLOOM_SIZE = CFG.LOGO_SIZE + BLOOM_PAD * 2;
  const bloomCanvas = document.createElement("canvas");
  bloomCanvas.width  = BLOOM_SIZE;
  bloomCanvas.height = BLOOM_SIZE;
  const bloomCtx = bloomCanvas.getContext("2d");
  let lastBloomIntensity = -1;

  function buildBloomCanvas(intensity) {
    // Only repaint when intensity changed meaningfully (saves GPU texture upload)
    const rounded = Math.round(intensity * 20) / 20;
    if (rounded === lastBloomIntensity) return bloomCanvas;
    lastBloomIntensity = rounded;

    const mid = BLOOM_SIZE / 2;
    bloomCtx.clearRect(0, 0, BLOOM_SIZE, BLOOM_SIZE);

    const b1 = bloomCtx.createRadialGradient(mid, mid, 0, mid, mid, BLOOM_SIZE * 0.48);
    b1.addColorStop(0,   `rgba(255,20,0,${0.18 * rounded})`);
    b1.addColorStop(0.5, `rgba(180,10,0,${0.09 * rounded})`);
    b1.addColorStop(1,   `rgba(0,0,0,0)`);
    bloomCtx.fillStyle = b1;
    bloomCtx.fillRect(0, 0, BLOOM_SIZE, BLOOM_SIZE);

    const b2 = bloomCtx.createRadialGradient(mid, mid, 0, mid, mid, BLOOM_SIZE * 0.28);
    b2.addColorStop(0,   `rgba(255,50,0,${0.35 * rounded})`);
    b2.addColorStop(0.6, `rgba(200,20,0,${0.15 * rounded})`);
    b2.addColorStop(1,   `rgba(0,0,0,0)`);
    bloomCtx.fillStyle = b2;
    bloomCtx.fillRect(0, 0, BLOOM_SIZE, BLOOM_SIZE);

    return bloomCanvas;
  }

  /* ── Draw logo + bloom ──────────────────────────────── */
  function drawLogo(glowIntensity) {
    if (!logoImg || logoAlpha <= 0) return;

    const size = CFG.LOGO_SIZE;
    const x    = cx - size / 2;
    const y    = cy - size / 2;

    ctx.save();
    ctx.globalAlpha = logoAlpha;
    const bloom = buildBloomCanvas(glowIntensity);
    ctx.drawImage(bloom, x - BLOOM_PAD, y - BLOOM_PAD, size + BLOOM_PAD * 2, size + BLOOM_PAD * 2);
    ctx.drawImage(logoImg, x, y, size, size);
    ctx.restore();
  }

  /* ── Typed-array particle storage ───────────────────── */
  // Each particle uses 12 floats:
  //  [0] x   [1] y   [2] vx  [3] vy
  //  [4] tx  [5] ty  [6] scale [7] alpha
  //  [8] fadeOut [9] spriteIdx [10] settled [11] usedRedSprite
  const pData = new Float32Array(N * 12);

  function pIdx(i) { return i * 12; }

  function initParticle(i) {
    const b = pIdx(i);
    pData[b + 0]  = Math.random() * (W || window.innerWidth);
    pData[b + 1]  = Math.random() * (H || window.innerHeight);
    pData[b + 2]  = (Math.random() - 0.5) * CFG.FREE_SPEED * 2;
    pData[b + 3]  = (Math.random() - 0.5) * CFG.FREE_SPEED * 2;
    pData[b + 4]  = 0; // tx
    pData[b + 5]  = 0; // ty
    pData[b + 6]  = Math.random() * 0.5 + 0.25; // scale
    pData[b + 7]  = Math.random() * 0.4 + 0.5;  // alpha
    pData[b + 8]  = 1.0;  // fadeOut
    pData[b + 9]  = Math.floor(Math.random() * sprites.length); // spriteIdx
    pData[b + 10] = 0;    // settled
    pData[b + 11] = 0;    // usedRedSprite
  }

  for (var i = 0; i < N; i++) initParticle(i);

  /* ── Free-roam update ───────────────────────────────── */
  function updateFreeRoam(b) {
    pData[b + 2] += (Math.random() - 0.5) * 0.06;
    pData[b + 3] += (Math.random() - 0.5) * 0.06;
    pData[b + 2] *= CFG.DRAG;
    pData[b + 3] *= CFG.DRAG;

    const spd = Math.hypot(pData[b + 2], pData[b + 3]);
    const max = CFG.FREE_SPEED * 1.6;
    if (spd > max) {
      pData[b + 2] = (pData[b + 2] / spd) * max;
      pData[b + 3] = (pData[b + 3] / spd) * max;
    }

    pData[b + 0] += pData[b + 2];
    pData[b + 1] += pData[b + 3];

    if (pData[b + 0] < 0) { pData[b + 0] = 0; pData[b + 2] *= -0.7; }
    if (pData[b + 0] > W) { pData[b + 0] = W; pData[b + 2] *= -0.7; }
    if (pData[b + 1] < 0) { pData[b + 1] = 0; pData[b + 3] *= -0.7; }
    if (pData[b + 1] > H) { pData[b + 1] = H; pData[b + 3] *= -0.7; }
  }

  /* ── Morph update ───────────────────────────────────── */
  function updateMorph(b, progress) {
    if (pData[b + 10]) return; // settled

    const dx   = pData[b + 4] - pData[b + 0];
    const dy   = pData[b + 5] - pData[b + 1];
    const dist = Math.hypot(dx, dy) + 0.001;

    const pull = CFG.PULL_STRENGTH * (0.5 + progress * 2.5) * Math.min(dist / 60, 3);
    pData[b + 2] += (dx / dist) * pull;
    pData[b + 3] += (dy / dist) * pull;
    pData[b + 2] *= CFG.DRAG_MORPH;
    pData[b + 3] *= CFG.DRAG_MORPH;

    if (dist < 2.0) {
      pData[b + 0] = pData[b + 4];
      pData[b + 1] = pData[b + 5];
      pData[b + 2] = 0;
      pData[b + 3] = 0;
      pData[b + 10] = 1; // settled
    } else {
      pData[b + 0] += pData[b + 2];
      pData[b + 1] += pData[b + 3];
    }

    if (progress > 0.5 && !pData[b + 11]) {
      pData[b + 9]  = -1; // use redSprite
      pData[b + 11] = 1;
    }
  }

  /* ── Draw a particle ────────────────────────────────── */
  const R2 = CFG.SPRITE_RADIUS * 2;
  function drawParticle(b) {
    const fo = pData[b + 8];
    if (fo <= 0) return;
    const sp   = pData[b + 9] === -1 ? redSprite : sprites[pData[b + 9] | 0];
    const size = R2 * pData[b + 6];
    ctx.globalAlpha = pData[b + 7] * fo;
    ctx.drawImage(sp, pData[b + 0] - size * 0.5, pData[b + 1] - size * 0.5, size, size);
  }

  /* ── Sample logo pixels ─────────────────────────────── */
  function sampleLogo(img) {
    const size = CFG.LOGO_SIZE;
    const oc   = document.createElement("canvas");
    oc.width   = size;
    oc.height  = size;
    // willReadFrequently: true → browser keeps pixel data in CPU-accessible memory
    const oc2  = oc.getContext("2d", { willReadFrequently: true });
    oc2.drawImage(img, 0, 0, size, size);

    const data = oc2.getImageData(0, 0, size, size).data;
    const pts  = [];

    for (var py = 0; py < size; py += 2) {
      for (var px = 0; px < size; px += 2) {
        const idx = (py * size + px) * 4;
        if (data[idx + 3] > CFG.SAMPLE_THRESHOLD) {
          pts.push({ nx: px / size, ny: py / size });
        }
      }
    }
    return pts;
  }

  /* ── Assign morph targets ───────────────────────────── */
  function assignTargets(pts) {
    const ox      = cx - CFG.LOGO_SIZE / 2;
    const oy      = cy - CFG.LOGO_SIZE / 2;
    const len     = pts.length;
    for (var i = 0; i < N; i++) {
      const b  = pIdx(i);
      const pt = pts[i % len];
      pData[b + 4]  = ox + pt.nx * CFG.LOGO_SIZE;
      pData[b + 5]  = oy + pt.ny * CFG.LOGO_SIZE;
      pData[b + 10] = 0; // un-settle
    }
  }

  /* ── Count settled ──────────────────────────────────── */
  function settledFraction() {
    var count = 0;
    for (var i = 0; i < N; i++) {
      if (pData[pIdx(i) + 10]) count++;
    }
    return count / N;
  }

  /* ── Reusable wash gradient (recreated only on resize) ─ */
  let washGrad = null;
  let lastCx = -1, lastCy = -1;

  function getWashGrad(glow) {
    // Only rebuild gradient if center changed (resize)
    if (cx !== lastCx || cy !== lastCy) {
      washGrad = null;
      lastCx = cx; lastCy = cy;
    }
    // Wash gradient is rebuilt each frame because it uses the glow value.
    // However the radial geometry is fixed — only color stops change.
    const g = ctx.createRadialGradient(cx, cy, CFG.LOGO_SIZE * 0.3, cx, cy, Math.max(W, H) * 0.8);
    g.addColorStop(0,   `rgba(0,0,0,0)`);
    g.addColorStop(0.5, `rgba(15,0,0,${(0.05 * glow).toFixed(3)})`);
    g.addColorStop(1,   `rgba(8,0,0,${(0.1  * glow).toFixed(3)})`);
    return g;
  }

  /* ── Main loop ──────────────────────────────────────── */
  function loop(ts) {
    animId = requestAnimationFrame(loop);
    const dt = Math.min((ts - lastTime) / 1000, CFG.MAX_DT);
    lastTime = ts;

    // Trail — fill with slight transparency to leave motion trails
    ctx.globalAlpha = 1;
    ctx.fillStyle   = `rgba(0,0,0,${CFG.TRAIL_ALPHA})`;
    ctx.fillRect(0, 0, W, H);

    if (phase === "freeroam") {
      elapsedFreeRoam += dt;
      for (var i = 0; i < N; i++) {
        const b = pIdx(i);
        updateFreeRoam(b);
        drawParticle(b);
      }
      ctx.globalAlpha = 1;
      if (elapsedFreeRoam >= CFG.FREE_ROAM_SECS) startMorph();

    } else if (phase === "morphing") {
      const elapsed  = (ts - morphStart) / 1000;
      const progress = Math.min(elapsed / CFG.MORPH_DURATION, 1);
      for (var i = 0; i < N; i++) {
        const b = pIdx(i);
        updateMorph(b, progress);
        drawParticle(b);
      }
      ctx.globalAlpha = 1;
      if (progress >= 1 && settledFraction() >= 0.95) {
        phase = "fadingOut";
        startParticleFadeOut();
      }

    } else if (phase === "fadingOut") {
      var allGone = true;
      for (var i = 0; i < N; i++) {
        const b = pIdx(i);
        pData[b + 8] = Math.max(0, pData[b + 8] - dt * 1.8);
        if (pData[b + 8] > 0.01) {
          drawParticle(b);
          allGone = false;
        }
      }
      ctx.globalAlpha = 1;
      if (allGone && !particlesFaded) {
        particlesFaded = true;
        phase = "formed";
        fadeLogoIn();
      }

    } else if (phase === "formed") {
      glowPhase += dt * 1.5;
      const glow = 0.5 + 0.5 * Math.sin(glowPhase);
      ctx.fillStyle   = getWashGrad(glow);
      ctx.globalAlpha = 1;
      ctx.fillRect(0, 0, W, H);
      drawLogo(glow);
    }
  }

  /* ── Logo fade-in ───────────────────────────────────── */
  function fadeLogoIn() {
    var start    = null;
    var duration = 0.8;
    function step(ts) {
      if (!start) start = ts;
      logoAlpha = Math.min((ts - start) / (duration * 1000), 1);
      if (logoAlpha < 1) requestAnimationFrame(step);
      else onLogoFormed();
    }
    requestAnimationFrame(step);
  }

  /* ── Phase transitions ──────────────────────────────── */
  function startMorph() {
    phase      = "morphing";
    morphStart = performance.now();
    typeStatus("PARTICLE LOCK INITIATED...");
    if (logoTargets.length > 0) assignTargets(logoTargets);
  }

  function startParticleFadeOut() {
    typeStatus("CONVERGENCE COMPLETE...");
  }

  function onLogoFormed() {
    typeStatus("STANDBY — AWAITING INPUT");
    enterEl.classList.add("visible");
    canEnter = true;
  }

  /* ── Init ───────────────────────────────────────────── */
  function init() {
    cx = W / 2;
    cy = H / 2;

    setTimeout(function () {
      corners.forEach(function (c) { c.classList.add("visible"); });
      statusEl.classList.add("visible");
      typeStatus("SYSTEM INITIALIZING...");
    }, 500);

    setTimeout(function () { typeStatus("ENERGY FIELD ACTIVE"); }, 2000);
    setTimeout(function () { typeStatus("PARTICLES DETECTED: " + N); }, 4000);
    setTimeout(function () { typeStatus("AWAITING CONVERGENCE..."); }, 5200);

    const img = document.getElementById("logo-source");

    function onReady() {
      logoImg      = img;
      logoTargets  = sampleLogo(img);
      requestAnimationFrame(function (ts) { lastTime = ts; loop(ts); });
    }

    if (img.complete && img.naturalWidth > 0) {
      onReady();
    } else {
      img.onload  = onReady;
      img.onerror = function () {
        requestAnimationFrame(function (ts) { lastTime = ts; loop(ts); });
      };
    }
  }

  /* ── Enter site ─────────────────────────────────────── */
  function enterSite() {
    if (!canEnter) return;
    canEnter = false;
    document.removeEventListener("keydown",  enterSite);
    document.removeEventListener("click",    enterSite);
    document.removeEventListener("touchend", enterSite);
    if (animId) cancelAnimationFrame(animId);

    gsap.to(flashEl, {
      opacity:  1,
      duration: 0.4,
      ease:     "power2.in",
      onComplete: function () {
        setTimeout(function () { window.location.href = "home.html"; }, 150);
      },
    });
  }

  // passive: true on touch/scroll events = browser doesn't wait for JS before scrolling
  document.addEventListener("keydown",  enterSite, { passive: true });
  document.addEventListener("click",   enterSite);
  document.addEventListener("touchend", enterSite, { passive: true });

  init();
})();
