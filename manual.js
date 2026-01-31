document.addEventListener("DOMContentLoaded", function () {
  /* =========================================
     1. ACCORDION FUNCTIONALITY
     ========================================= */
  const sections = document.querySelectorAll(".manual-section");

  sections.forEach((section) => {
    const header = section.querySelector(".section-header");

    header.addEventListener("click", () => {
      // Option A: Close all other sections (This matches your current behavior)
      sections.forEach((otherSection) => {
        if (
          otherSection !== section &&
          otherSection.classList.contains("active")
        ) {
          otherSection.classList.remove("active");
        }
      });

      // Toggle current section
      section.classList.toggle("active");
    });
  });

  /* =========================================
     2. MOBILE MENU FUNCTIONALITY
     ========================================= */
  const mobileMenuToggle = document.querySelector(".mobile-menu-toggle");
  const navLinks = document.querySelector(".nav-links");

  if (mobileMenuToggle && navLinks) {
    mobileMenuToggle.addEventListener("click", () => {
      navLinks.classList.toggle("active");

      // Transform hamburger animation
      const spans = mobileMenuToggle.querySelectorAll("span");
      if (navLinks.classList.contains("active")) {
        // Turn into X
        spans[0].style.transform = "rotate(45deg) translate(5px, 5px)";
        spans[1].style.opacity = "0";
        spans[2].style.transform = "rotate(-45deg) translate(5px, -5px)";

        // Apply mobile styles dynamically
        navLinks.style.display = "flex";
        navLinks.style.flexDirection = "column";
        navLinks.style.position = "absolute";
        navLinks.style.top = "75px";
        navLinks.style.left = "0";
        navLinks.style.width = "100%";
        navLinks.style.backgroundColor = "rgba(10, 10, 10, 0.95)";
        navLinks.style.padding = "20px 0";
        navLinks.style.borderBottom = "1px solid #222";
        navLinks.style.textAlign = "center";
      } else {
        // Reset to Hamburger
        spans[0].style.transform = "none";
        spans[1].style.opacity = "1";
        spans[2].style.transform = "none";
        navLinks.removeAttribute("style");
      }
    });
  }
});
