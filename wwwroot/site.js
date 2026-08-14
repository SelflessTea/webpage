(() => {
  const counter = document.querySelector("#visitor-counter");
  if (!counter) return;

  fetch("/api/visitor-count", { method: "POST" })
    .then(response => response.ok ? response.json() : Promise.reject())
    .then(result => {
      counter.textContent = String(result.count).padStart(6, "0");
      counter.classList.remove("loading");
      counter.setAttribute("aria-busy", "false");
    })
    .catch(() => {
      counter.textContent = "??????";
      counter.classList.remove("loading");
      counter.setAttribute("aria-busy", "false");
    });
})();

(() => {
  const image = document.querySelector("#random-cat");
  const next = document.querySelector("#next-cat");
  const status = document.querySelector("#cat-status");
  if (!image || !next || !status) return;

  const loadNextCat = () => {
    next.disabled = true;
    status.textContent = "Finding another cat...";
    image.style.opacity = ".45";
    image.src = `/api/cat?next=${Date.now()}`;
  };

  image.addEventListener("load", () => {
    image.style.opacity = "1";
    next.disabled = false;
    status.textContent = "Please pet responsibly.";
  });

  image.addEventListener("error", () => {
    image.style.opacity = "1";
    next.disabled = false;
    status.textContent = "The cats are napping. Try again!";
  });

  next.addEventListener("click", loadNextCat);
})();

(() => {
  const canvas = document.querySelector("#paint-canvas");
  if (!canvas) return;

  const context = canvas.getContext("2d", { alpha: false });
  const pencil = document.querySelector("#paint-pencil");
  const eraser = document.querySelector("#paint-eraser");
  const clear = document.querySelector("#paint-clear");
  const swatches = [...document.querySelectorAll(".paint-swatch")];
  const sizeButtons = [...document.querySelectorAll(".brush-size")];
  const messageToggle = document.querySelector("#message-toggle");
  const messageBox = document.querySelector("#drawing-message-box");
  const message = document.querySelector("#drawing-message");
  const messageCount = document.querySelector("#message-count");
  const send = document.querySelector("#paint-send");
  const status = document.querySelector("#paint-status");

  let drawing = false;
  let erasing = false;
  let selectedColor = "#000000";
  let brushSize = 5;
  let lastPoint = null;

  const fillWhite = () => {
    context.save();
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.restore();
  };

  const pointFromEvent = event => {
    const bounds = canvas.getBoundingClientRect();
    return {
      x: (event.clientX - bounds.left) * (canvas.width / bounds.width),
      y: (event.clientY - bounds.top) * (canvas.height / bounds.height)
    };
  };

  const chooseTool = useEraser => {
    erasing = useEraser;
    pencil.classList.toggle("active", !useEraser);
    eraser.classList.toggle("active", useEraser);
  };

  canvas.addEventListener("pointerdown", event => {
    drawing = true;
    lastPoint = pointFromEvent(event);
    canvas.setPointerCapture(event.pointerId);
  });

  canvas.addEventListener("pointermove", event => {
    if (!drawing || !lastPoint) return;

    const point = pointFromEvent(event);
    context.beginPath();
    context.moveTo(lastPoint.x, lastPoint.y);
    context.lineTo(point.x, point.y);
    context.strokeStyle = erasing ? "#ffffff" : selectedColor;
    context.lineWidth = brushSize;
    context.lineCap = "round";
    context.lineJoin = "round";
    context.stroke();
    lastPoint = point;
  });

  const stopDrawing = () => {
    drawing = false;
    lastPoint = null;
  };

  canvas.addEventListener("pointerup", stopDrawing);
  canvas.addEventListener("pointercancel", stopDrawing);
  pencil.addEventListener("click", () => chooseTool(false));
  eraser.addEventListener("click", () => chooseTool(true));
  clear.addEventListener("click", fillWhite);

  swatches.forEach(swatch => {
    swatch.addEventListener("click", () => {
      selectedColor = swatch.dataset.color;
      swatches.forEach(item => item.classList.toggle("active", item === swatch));
      chooseTool(false);
    });
  });

  sizeButtons.forEach(button => {
    button.addEventListener("click", () => {
      brushSize = Number(button.dataset.size);
      sizeButtons.forEach(item => item.classList.toggle("active", item === button));
    });
  });

  messageToggle.addEventListener("click", () => {
    const willOpen = messageBox.hidden;
    messageBox.hidden = !willOpen;
    messageToggle.setAttribute("aria-expanded", String(willOpen));
    if (willOpen) message.focus();
  });

  message.addEventListener("input", () => {
    messageCount.textContent = `${message.value.length} / 1000`;
  });

  send.addEventListener("click", () => {
    send.disabled = true;
    status.className = "";
    status.textContent = "Sending...";

    canvas.toBlob(async blob => {
      try {
        if (!blob) throw new Error("Could not create the drawing.");

        const form = new FormData();
        form.append("drawing", blob, "drawing.png");
        if (message.value.trim()) form.append("message", message.value.trim());

        const response = await fetch("/api/drawings", {
          method: "POST",
          body: form
        });
        const result = await response.json();

        if (!response.ok)
          throw new Error(result.error ?? result.detail ?? "Could not send the drawing.");

        status.className = "success";
        status.textContent = "Thank you so much~ :3";
        send.hidden = true;
      } catch (error) {
        status.className = "error";
        status.textContent = error.message;
      } finally {
        send.disabled = false;
      }
    }, "image/png");
  });

  fillWhite();
})();
