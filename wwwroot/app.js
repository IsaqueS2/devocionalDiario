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
participantInput.addEventListener("input", () => {
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

function dateKey(year, month, day) {
  return `${year}-${String(month + 1).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}

function createDayCard(day, year, month) {
  const date = new Date(year, month, day);
  const key = dateKey(year, month, day);
  const people = records[key] || [];
  const currentName = participantInput.value.trim();
  const hasMarked = currentName.length > 0 && people.some((person) => person.toLocaleLowerCase() === currentName.toLocaleLowerCase());
  const card = document.createElement("article");
  card.className = "day-card";
  if (dateKey(year, month, day) === dateKey(now.getFullYear(), now.getMonth(), now.getDate())) {
    card.classList.add("is-today");
  }

  const number = document.createElement("span");
  number.className = "day-number";
  number.textContent = String(day);
  card.append(number);

  if (people.length > 0) {
    const peopleList = document.createElement("div");
    peopleList.className = "day-people";
    peopleList.setAttribute("aria-label", `Registrado por ${people.join(", ")}`);
    for (const person of people) {
      const chip = document.createElement("span");
      chip.className = "person-chip";
      chip.textContent = person;
      peopleList.append(chip);
    }
    card.append(peopleList);
  }

  const action = document.createElement("button");
  action.type = "button";
  action.className = `day-action${hasMarked ? " is-marked" : ""}`;
  action.textContent = isLoading ? "..." : hasMarked ? "✓ Registrado" : "+ Marcar";
  action.disabled = isLoading || !currentName;
  action.setAttribute("aria-label", `${hasMarked ? "Desmarcar" : "Marcar"} devocional em ${date.toLocaleDateString("pt-BR")}`);
  action.addEventListener("click", () => toggleRecord(key));
  card.append(action);

  return card;
}

function renderCalendar() {
  const year = displayedMonth.getFullYear();
  const month = displayedMonth.getMonth();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const mondayFirstOffset = (new Date(year, month, 1).getDay() + 6) % 7;
  const monthName = displayedMonth.toLocaleDateString("pt-BR", { month: "long", year: "numeric" });

  monthTitle.textContent = monthName;
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
  progressCaption.textContent = `${recordedDays} ${recordedDays === 1 ? "dia registrado" : "dias registrados"} de ${daysInMonth}`;
  const percentage = Math.round((recordedDays / daysInMonth) * 100);
  progressFill.style.width = `${percentage}%`;
  progressBar.setAttribute("aria-valuenow", String(percentage));
}

async function loadMonth() {
  const year = displayedMonth.getFullYear();
  const month = displayedMonth.getMonth() + 1;
  isLoading = true;
  statusMessage.textContent = "";
  renderCalendar();

  try {
    const response = await fetch(`/api/devocional/${year}/${month}`);
    if (!response.ok) {
      throw new Error("Não foi possível carregar os registros deste mês.");
    }
    records = await response.json();
    isLoading = false;
    renderCalendar();
  } catch (error) {
    isLoading = false;
    records = {};
    renderCalendar();
    statusMessage.textContent = error instanceof Error
      ? error.message
      : "Não foi possível conectar ao aplicativo. Tente novamente.";
  }
}

async function toggleRecord(date) {
  const usuario = participantInput.value.trim();
  if (!usuario) {
    statusMessage.textContent = "Digite seu nome antes de registrar um devocional.";
    participantInput.focus();
    return;
  }

  isLoading = true;
  statusMessage.textContent = "";
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
    statusMessage.textContent = error instanceof Error
      ? error.message
      : "Não foi possível salvar o registro. Tente novamente.";
  }
}

loadMonth();
