document.addEventListener("DOMContentLoaded", function () {
  /* =========================================
     1. NAVIGATION & MOBILE MENU
     ========================================= */
  const mobileToggle = document.querySelector(".mobile-menu-toggle");
  const navLinks = document.querySelector(".nav-links");

  if (mobileToggle && navLinks) {
    mobileToggle.addEventListener("click", () => {
      const isExpanded = navLinks.classList.contains("active");

      // Toggle Classes
      navLinks.classList.toggle("active");
      mobileToggle.classList.toggle("active");
      mobileToggle.setAttribute("aria-expanded", !isExpanded);

      // Animate Hamburger
      const spans = mobileToggle.querySelectorAll("span");
      if (!isExpanded) {
        spans[0].style.transform = "rotate(45deg) translate(8px, 8px)";
        spans[1].style.opacity = "0";
        spans[2].style.transform = "rotate(-45deg) translate(7px, -7px)";
      } else {
        spans.forEach((span) => {
          span.style.transform = "none";
          span.style.opacity = "1";
        });
      }
    });
  }

  /* =========================================
     2. ACCORDION / EXPANDABLE LOGIC
     ========================================= */
  // Handles both Policies buttons and Manual Accordion headers
  const expandTriggers = document.querySelectorAll(
    ".expand-button, .accordion-header"
  );

  expandTriggers.forEach((trigger) => {
    trigger.addEventListener("click", function () {
      const content = this.nextElementSibling;
      const isActive = this.classList.contains("active");
      const innerContent = content.querySelector("div");

      if (isActive) {
        // Close
        content.style.height = "0";
        content.classList.remove("expanded");
        this.classList.remove("active");
        this.setAttribute("aria-expanded", "false");
      } else {
        // Open
        this.classList.add("active");
        this.setAttribute("aria-expanded", "true");
        content.classList.add("expanded");
        // Dynamic Height Calculation
        content.style.height = innerContent.scrollHeight + "px";
      }
    });
  });

  /* =========================================
     3. MANUAL TABLE OF CONTENTS (TOC)
     ========================================= */
  const tocLinks = document.querySelectorAll(".toc-link");
  if (tocLinks.length > 0) {
    // Smooth Scroll on Click
    tocLinks.forEach((link) => {
      link.addEventListener("click", function (e) {
        e.preventDefault();
        const targetId = this.getAttribute("href").substring(1);
        const targetElement = document.getElementById(targetId); // This is the content div

        if (targetElement) {
          // Find the button controlling this content
          const trigger = targetElement.previousElementSibling;

          // Open if closed
          if (!trigger.classList.contains("active")) {
            trigger.click();
          }

          // Scroll
          setTimeout(() => {
            trigger.scrollIntoView({ behavior: "smooth", block: "center" });
          }, 100);
        }
      });
    });

    // Scroll Spy (Highlight TOC based on scroll)
    window.addEventListener("scroll", () => {
      const scrollPos = window.scrollY + 200;
      let currentId = "";

      document.querySelectorAll(".accordion-item").forEach((item) => {
        if (item.offsetTop <= scrollPos) {
          // Find the ID of the content inside this item
          const content = item.querySelector(".accordion-content");
          if (content) currentId = content.id;
        }
      });

      tocLinks.forEach((link) => {
        link.classList.remove("active");
        if (link.getAttribute("href") === "#" + currentId) {
          link.classList.add("active");
        }
      });
    });
  }

  /* =========================================
     4. BACK TO TOP BUTTON
     ========================================= */
  const backToTopBtn = document.querySelector(".back-to-top");
  if (backToTopBtn) {
    window.addEventListener("scroll", () => {
      if (window.scrollY > 300) {
        backToTopBtn.classList.add("visible");
      } else {
        backToTopBtn.classList.remove("visible");
      }
    });

    backToTopBtn.addEventListener("click", () => {
      window.scrollTo({ top: 0, behavior: "smooth" });
    });
  }

  /* =========================================
     5. GENERAL ANIMATIONS (Fade In)
     ========================================= */
  const animatedElements = document.querySelectorAll(
    ".feature-card, .policy-card"
  );
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.style.opacity = "1";
          entry.target.style.transform = "translateY(0)";
          observer.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.1 }
  );

  animatedElements.forEach((el) => {
    el.style.opacity = "0";
    el.style.transform = "translateY(20px)";
    el.style.transition = "opacity 0.6s ease, transform 0.6s ease";
    observer.observe(el);
  });
});
