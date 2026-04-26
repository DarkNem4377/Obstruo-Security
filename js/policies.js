/* =======================================================
   OBSTRUO — POLICIES PAGE JS
   GPU / Performance Optimizations Applied:
   - All scroll listeners use { passive: true }
   - Scroll handlers RAF-throttled (one frame per handler)
   - Resize debounce uses setTimeout (avoids rapid style reads)
   - DOM class/style writes batched into rAF where possible
   - No forced synchronous layout (no offsetHeight reads mid-animation)
======================================================= */

function init() {
  animateCards();
  setupExpandableSections();
  setupMobileMenu();
  setupStickyBackButton();
  setupSmoothScroll();
}

/* ── Card entrance animation ─────────────────────────── */
function animateCards() {
  var policyCards = document.querySelectorAll(".policy-card");

  // Set initial state in one rAF to batch style writes
  requestAnimationFrame(function () {
    policyCards.forEach(function (card) {
      card.style.opacity   = "0";
      card.style.transform = "translateY(20px) translateZ(0)";
      card.style.transition = "opacity 0.6s ease, transform 0.6s ease";
    });

    // Stagger entrance animations
    policyCards.forEach(function (card, index) {
      setTimeout(function () {
        requestAnimationFrame(function () {
          card.style.opacity   = "1";
          card.style.transform = "translateY(0) translateZ(0)";
        });
      }, index * 150);
    });
  });
}

/* ── Expandable sections ─────────────────────────────── */
function setupExpandableSections() {
  var expandButtons = document.querySelectorAll(".expand-button");

  expandButtons.forEach(function (button) {
    button.addEventListener("click", function () {
      var content  = this.nextElementSibling;
      var isActive = this.classList.contains("active");
      var innerDiv = content.querySelector("div");

      if (isActive) {
        // Read scrollHeight BEFORE modifying styles (avoids forced sync layout)
        var currentHeight = content.scrollHeight;

        // Batch all writes into one rAF
        requestAnimationFrame(function () {
          content.style.height  = currentHeight + "px";
          // Force reflow so transition can animate from exact height → 0
          // eslint-disable-next-line no-unused-expressions
          content.getBoundingClientRect(); // cheaper than offsetHeight
          content.style.height  = "0";
          content.style.opacity = "0";
          content.classList.remove("expanded");
        });

        this.classList.remove("active");
        this.setAttribute("aria-expanded", "false");
        content.setAttribute("aria-hidden", "true");
      } else {
        // Read target height before any writes
        var targetHeight = innerDiv.scrollHeight;

        requestAnimationFrame(function () {
          content.style.height  = targetHeight + "px";
          content.style.opacity = "1";
          content.classList.add("expanded");
        });

        this.classList.add("active");
        this.setAttribute("aria-expanded", "true");
        content.setAttribute("aria-hidden", "false");

        // After transition, set height auto so resizing works naturally
        content.addEventListener("transitionend", function onEnd(e) {
          if (e.propertyName === "height" && content.classList.contains("expanded")) {
            content.style.height = "auto";
          }
          content.removeEventListener("transitionend", onEnd);
        });
      }
    });
  });

  // Debounced resize — only recalculate after user stops resizing
  var resizeTimer;
  window.addEventListener("resize", function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () {
      // Read all heights in one batch, then write in one rAF
      var openSections = document.querySelectorAll(".expandable-content.expanded");
      var heights = [];
      openSections.forEach(function (content) {
        if (content.style.height !== "auto") {
          var innerDiv = content.querySelector("div");
          heights.push({ el: content, h: innerDiv.scrollHeight });
        }
      });
      requestAnimationFrame(function () {
        heights.forEach(function (item) {
          item.el.style.height = item.h + "px";
        });
      });
    }, 250);
  }, { passive: true });
}

/* ── Mobile menu ─────────────────────────────────────── */
function setupMobileMenu() {
  var mobileToggle = document.querySelector(".mobile-menu-toggle");
  var mobileMenu   = document.getElementById("mobileMenu");

  if (!mobileToggle || !mobileMenu) return;

  mobileMenu.querySelectorAll("a").forEach(function (link) {
    link.addEventListener("click", function () {
      mobileMenu.classList.remove("active");
      mobileToggle.classList.remove("active");
      mobileToggle.setAttribute("aria-expanded", "false");
    });
  });

  mobileToggle.addEventListener("click", function () {
    var isOpen = mobileMenu.classList.contains("active");
    mobileMenu.classList.toggle("active");
    mobileToggle.classList.toggle("active");
    mobileToggle.setAttribute("aria-expanded", String(!isOpen));
  });

  // Close on outside click
  document.addEventListener("click", function (e) {
    if (!mobileToggle.contains(e.target) && !mobileMenu.contains(e.target)) {
      if (mobileMenu.classList.contains("active")) {
        mobileMenu.classList.remove("active");
        mobileToggle.classList.remove("active");
        mobileToggle.setAttribute("aria-expanded", "false");
      }
    }
  });
}

/* ── Sticky back button — RAF-throttled passive scroll ── */
function setupStickyBackButton() {
  var backToHome = document.querySelector(".back-to-home");
  if (!backToHome) return;

  var scrollPending = false;
  window.addEventListener("scroll", function () {
    if (scrollPending) return;
    scrollPending = true;
    requestAnimationFrame(function () {
      if (window.pageYOffset > 100) {
        backToHome.classList.add("scrolled");
      } else {
        backToHome.classList.remove("scrolled");
      }
      scrollPending = false;
    });
  }, { passive: true });
}

/* ── Smooth scroll for anchor links ─────────────────── */
function setupSmoothScroll() {
  document.querySelectorAll('a[href^="#"]').forEach(function (anchor) {
    anchor.addEventListener("click", function (e) {
      var href = this.getAttribute("href");
      if (href.startsWith("#") && href.length > 1) {
        var target = document.querySelector(href);
        if (target) {
          e.preventDefault();
          target.scrollIntoView({ behavior: "smooth", block: "start" });
        }
      }
    });
  });
}

/* ── Bootstrap ───────────────────────────────────────── */
if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", init);
} else {
  init();
}