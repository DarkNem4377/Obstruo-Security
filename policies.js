// Initialize app
function init() {
  animateCards();
  setupExpandableSections();
  setupMobileMenu();
  setupStickyBackButton();
  setupSmoothScroll();
}

// Card animation with staggered delay
function animateCards() {
  const policyCards = document.querySelectorAll(".policy-card");
  policyCards.forEach((card, index) => {
    setTimeout(() => {
      card.classList.add("animate");
    }, index * 100);
  });
}

// Enhanced expandable sections with dynamic height calculation
function setupExpandableSections() {
  const expandButtons = document.querySelectorAll(".expand-button");

  expandButtons.forEach((button) => {
    button.addEventListener("click", function () {
      const content = this.nextElementSibling;
      const isActive = this.classList.contains("active");
      const innerDiv = content.querySelector("div");

      if (isActive) {
        // Collapse
        content.style.height = content.scrollHeight + "px";
        requestAnimationFrame(() => {
          content.style.height = "0";
          content.classList.remove("expanded");
        });
        this.classList.remove("active");
        this.setAttribute("aria-expanded", "false");
        content.setAttribute("aria-hidden", "true");
      } else {
        // Expand
        const height = innerDiv.scrollHeight;
        content.style.height = height + "px";
        content.classList.add("expanded");
        this.classList.add("active");
        this.setAttribute("aria-expanded", "true");
        content.setAttribute("aria-hidden", "false");
      }
    });
  });

  // Recalculate heights on window resize
  let resizeTimer;
  window.addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      document
        .querySelectorAll(".expandable-content.expanded")
        .forEach((content) => {
          const innerDiv = content.querySelector("div");
          content.style.height = innerDiv.scrollHeight + "px";
        });
    }, 250);
  });
}

// Mobile menu toggle
function setupMobileMenu() {
  const mobileMenuToggle = document.querySelector(".mobile-menu-toggle");
  const navLinks = document.querySelector(".nav-links");

  if (mobileMenuToggle) {
    mobileMenuToggle.addEventListener("click", function () {
      const isExpanded = this.getAttribute("aria-expanded") === "true";
      this.setAttribute("aria-expanded", String(!isExpanded));
      navLinks.classList.toggle("active");
      navLinks.setAttribute("aria-hidden", String(isExpanded));

      // Animate hamburger icon
      const spans = this.querySelectorAll("span");
      if (!isExpanded) {
        spans[0].style.transform = "rotate(45deg) translate(5px, 5px)";
        spans[1].style.opacity = "0";
        spans[2].style.transform = "rotate(-45deg) translate(7px, -6px)";
      } else {
        spans.forEach((span) => {
          span.style.transform = "none";
          span.style.opacity = "1";
        });
      }
    });

    // Close menu when clicking outside
    document.addEventListener("click", (e) => {
      if (
        !mobileMenuToggle.contains(e.target) &&
        !navLinks.contains(e.target)
      ) {
        if (navLinks.classList.contains("active")) {
          mobileMenuToggle.click();
        }
      }
    });
  }
}

// Sticky back button behavior
function setupStickyBackButton() {
  const backToHome = document.querySelector(".back-to-home");
  let lastScroll = 0;

  window.addEventListener("scroll", function () {
    const currentScroll = window.pageYOffset;

    if (currentScroll > 100) {
      backToHome.classList.add("scrolled");
    } else {
      backToHome.classList.remove("scrolled");
    }

    lastScroll = currentScroll;
  });
}

// Smooth scroll for anchor links
function setupSmoothScroll() {
  document.querySelectorAll('a[href^="#"]').forEach((anchor) => {
    anchor.addEventListener("click", function (e) {
      const href = this.getAttribute("href");
      if (href.startsWith("#") && href.length > 1) {
        const target = document.querySelector(href);
        if (target) {
          e.preventDefault();
          target.scrollIntoView({
            behavior: "smooth",
            block: "start",
          });
        }
      }
    });
  });
}

// Initialize on DOM ready
if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", init);
} else {
  init();
}
