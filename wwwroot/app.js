const monthTitle = document.querySelector("#month-title");
const calendar = document.querySelector("#calendar");
const participantInput = document.querySelector("#participant-name");
const statusMessage = document.querySelector("#status-message");
const progressCaption = document.querySelector("#progress-caption");
const progressCount = document.querySelector("#progress-count");
const progressFill = document.querySelector("#progress-fill");
const progressBar = document.querySelector(".progress-track");

const now = new Date();
let displayedMonth = new Date(now.getFullYear(), now.getMonth(), 1);
let records = {};
let isLoading = false;

function setStatus(message, isError = false) {
  statusMessage.textContent = message;
  statusMessage.classList.toggle("is-error", isError);
}

try {
  participantInput.value = localStorage.getItem("devocional.participant") || "";
} catch {
  // O nome continua disponível durante esta sessão se o armazenamento do navegador estiver bloqueado.
}

document.querySelector("#previous-month").addEventListener("click", () => changeMonth(-1));
document.querySelector("#next-month").addEventListener("click", () => changeMonth(1));
document.querySelector("#go-today").addEventListener("click", () => {
  displayedMonth = new Date(now.getFullYear(), now.getMonth(), 1);
  loadMonth();
});
document.querySelector("#name-form").addEventListener("submit", (event) => {
  event.preventDefault();
  participantInput.blur();
});
window.addEventListener("resize", updateMonthTitle);
participantInput.addEventListener("input", () => {
  setStatus("");
  try {
    localStorage.setItem("devocional.participant", participantInput.value.trim());
  } catch {
    // O cadastro no banco continua funcionando sem persistir esta preferência no navegador.
  }
  renderCalendar();
});

function changeMonth(offset) {
  displayedMonth = new Date(displayedMonth.getFullYear(), displayedMonth.getMonth() + offset, 1);
  loadMonth();
}

function updateMonthTitle() {
  const options = window.matchMedia("(max-width: 430px)").matches
    ? { month: "short", year: "numeric" }
    : { month: "long", year: "numeric" };
  monthTitle.textContent = displayedMonth.toLocaleDateString("pt-BR", options);
}

function dateKey(year, month, day) {
  return `${year}-${String(month + 1).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}

function createDayCard(day, year, month) {
  const date = new Date(year, month, day);
  const key = dateKey(year, month, day);
  const people = records[key] || [];
  const currentName = participantInput.value.trim();
  const hasMarked = currentName.length > 0 && people.some((person) => person.toLocaleLowerCase() === currentName.toLocaleLowerCase());
  const card = document.createElement("button");
  const dateLabel = date.toLocaleDateString("pt-BR", { day: "numeric", month: "long", year: "numeric" });
  card.className = "day-card";
  card.type = "button";
  card.disabled = isLoading;
  if (dateKey(year, month, day) === dateKey(now.getFullYear(), now.getMonth(), now.getDate())) {
    card.classList.add("is-today");
  }
  if (hasMarked) {
    card.classList.add("is-marked");
  }

  const number = document.createElement("span");
  number.className = "day-number";
  number.textContent = String(day);
  card.append(number);

  if (people.length > 0) {
    const markers = document.createElement("span");
    markers.className = "day-markers";
    markers.setAttribute("aria-hidden", "true");

    for (let index = 0; index < Math.min(people.length, 3); index += 1) {
      const marker = document.createElement("span");
      marker.className = "day-marker";
      markers.append(marker);
    }
    if (people.length > 3) {
      const more = document.createElement("span");
      more.className = "day-more";
      more.textContent = `+${people.length - 3}`;
      markers.append(more);
    }

    card.append(markers);
  } else {
    const markerSpace = document.createElement("span");
    markerSpace.className = "day-markers";
    markerSpace.setAttribute("aria-hidden", "true");
    card.append(markerSpace);
  }

  const action = hasMarked
    ? "Seu devocional está registrado. Toque para desmarcar."
    : "Toque para registrar seu devocional.";
  card.setAttribute(
    "aria-label",
    `${dateLabel}. ${people.length > 0 ? `${people.length} ${people.length === 1 ? "registro" : "registros"}: ${people.join(", ")}. ` : "Sem registros. "}${currentName ? action : "Digite seu nome para registrar."}`
  );
  card.setAttribute("aria-pressed", String(hasMarked));
  card.title = people.length > 0
    ? `${dateLabel}: ${people.join(", ")}`
    : `${dateLabel}: sem registros`;
  card.addEventListener("click", () => toggleRecord(key));

  return card;
}

function renderCalendar() {
  const year = displayedMonth.getFullYear();
  const month = displayedMonth.getMonth();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const mondayFirstOffset = (new Date(year, month, 1).getDay() + 6) % 7;

  updateMonthTitle();
  calendar.replaceChildren();

  for (let index = 0; index < mondayFirstOffset; index += 1) {
    const blank = document.createElement("div");
    blank.className = "day-card is-outside";
    blank.setAttribute("aria-hidden", "true");
    calendar.append(blank);
  }

  for (let day = 1; day <= daysInMonth; day += 1) {
    calendar.append(createDayCard(day, year, month));
  }

  const recordedDays = Object.values(records).filter((people) => people.length > 0).length;
  progressCount.textContent = `${recordedDays}/${daysInMonth}`;
  progressCount.setAttribute("aria-label", `${recordedDays} de ${daysInMonth} dias com registro`);
  progressCaption.textContent = `${recordedDays} ${recordedDays === 1 ? "dia com registro" : "dias com registro"} de ${daysInMonth}`;
  const percentage = Math.round((recordedDays / daysInMonth) * 100);
  progressFill.style.width = `${percentage}%`;
  progressBar.setAttribute("aria-valuenow", String(percentage));
}

async function loadMonth() {
  const year = displayedMonth.getFullYear();
  const month = displayedMonth.getMonth() + 1;
  isLoading = true;
  setStatus("Carregando os registros...");
  renderCalendar();

  try {
    const response = await fetch(`/api/devocional/${year}/${month}`);
    if (!response.ok) {
      throw new Error("Não foi possível carregar os registros deste mês.");
    }
    records = await response.json();
    isLoading = false;
    setStatus("");
    renderCalendar();
  } catch (error) {
    isLoading = false;
    records = {};
    renderCalendar();
    setStatus(error instanceof Error
      ? error.message
      : "Não foi possível conectar ao aplicativo. Tente novamente.", true);
  }
}

async function toggleRecord(date) {
  const usuario = participantInput.value.trim();
  if (!usuario) {
    setStatus("Digite seu nome antes de registrar um devocional.", true);
    participantInput.focus();
    return;
  }

  isLoading = true;
  setStatus("Salvando seu registro...");
  renderCalendar();

  try {
    const response = await fetch("/api/devocional/alternar", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ data: date, usuario })
    });
    if (!response.ok) {
      const body = await response.json();
      throw new Error(body.erro || "Não foi possível salvar o registro.");
    }
    await loadMonth();
  } catch (error) {
    isLoading = false;
    renderCalendar();
    setStatus(error instanceof Error
      ? error.message
      : "Não foi possível salvar o registro. Tente novamente.", true);
  }
}

loadMonth();
