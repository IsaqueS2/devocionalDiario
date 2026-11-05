const monthTitle = document.querySelector("#month-title");
const calendar = document.querySelector("#calendar");
const participantInput = document.querySelector("#participant-name");
const enableReminderButton = document.querySelector("#enable-reminder");
const disableReminderButton = document.querySelector("#disable-reminder");
const reminderStatus = document.querySelector("#reminder-status");
const recordDialog = document.querySelector("#record-dialog");
const recordForm = document.querySelector("#record-form");
const dialogTitle = document.querySelector("#record-dialog-title");
const themeInput = document.querySelector("#devotional-theme");
const dayRecords = document.querySelector("#day-records");
const dialogStatus = document.querySelector("#dialog-status");
const removeRecordButton = document.querySelector("#remove-record");
const saveRecordButton = document.querySelector("#save-record");
const statusMessage = document.querySelector("#status-message");
const progressCaption = document.querySelector("#progress-caption");
const progressCount = document.querySelector("#progress-count");
const progressFill = document.querySelector("#progress-fill");
const progressBar = document.querySelector(".progress-track");

const now = new Date();
let displayedMonth = new Date(now.getFullYear(), now.getMonth(), 1);
let records = {};
let isLoading = false;
let selectedDate = "";
let reminderRegistration = null;
let reminderSubscription = null;
let reminderSyncTimer = 0;

function setStatus(message, isError = false) {
  statusMessage.textContent = message;
  statusMessage.classList.toggle("is-error", isError);
}

function setReminderStatus(message, isError = false) {
  reminderStatus.textContent = message;
  reminderStatus.classList.toggle("is-error", isError);
}

function updateReminderControls() {
  const active = Boolean(reminderSubscription);
  enableReminderButton.hidden = active;
  disableReminderButton.hidden = !active;
}

async function initializeReminder() {
  if (!("serviceWorker" in navigator) ||
      !("PushManager" in window) ||
      !("Notification" in window)) {
    enableReminderButton.disabled = true;
    setReminderStatus("Este navegador não oferece suporte a notificações push.");
    return;
  }

  try {
    reminderRegistration = await navigator.serviceWorker.register("/service-worker.js");
    await navigator.serviceWorker.ready;
    reminderSubscription = await reminderRegistration.pushManager.getSubscription();
    updateReminderControls();
    if (reminderSubscription && participantInput.value.trim()) {
      try {
        await saveReminderSubscription(reminderSubscription);
        setReminderStatus("Lembrete ativo neste aparelho para as 22h30.");
      } catch (error) {
        setReminderStatus(error instanceof Error ? error.message : "Não foi possível atualizar o lembrete.", true);
      }
    } else if (reminderSubscription) {
      await removeSavedReminderSubscription(reminderSubscription);
    }
  } catch (error) {
    enableReminderButton.disabled = true;
    setReminderStatus(error instanceof Error
      ? `Não foi possível preparar as notificações: ${error.message}`
      : "Não foi possível preparar as notificações.", true);
  }
}

async function removeSavedReminderSubscription(subscription) {
  const response = await fetch("/api/notificacoes/inscricao", {
    method: "DELETE",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ endpoint: subscription.endpoint })
  });
  if (!response.ok) {
    throw new Error("Não foi possível atualizar a inscrição de notificações.");
  }
}

function decodeVapidPublicKey(value) {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, "="));
  return Uint8Array.from(raw, (character) => character.charCodeAt(0));
}

async function saveReminderSubscription(subscription) {
  const usuario = participantInput.value.trim();
  const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const response = await fetch("/api/notificacoes/inscricao", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ usuario, timezone, subscription: subscription.toJSON() })
  });
  if (!response.ok) {
    const body = await response.json();
    throw new Error(body.detail || body.erro || "Não foi possível salvar a inscrição de notificações.");
  }
}

async function enableReminder() {
  const usuario = participantInput.value.trim();
  if (!usuario) {
    setReminderStatus("Informe seu nome antes de ativar o lembrete.", true);
    participantInput.focus();
    return;
  }
  if (!window.isSecureContext) {
    setReminderStatus("As notificações precisam de uma conexão HTTPS.", true);
    return;
  }

  enableReminderButton.disabled = true;
  setReminderStatus("Preparando o lembrete...");
  try {
    if (!reminderRegistration) {
      reminderRegistration = await navigator.serviceWorker.register("/service-worker.js");
    }

    const keyResponse = await fetch("/api/notificacoes/chave-publica");
    if (!keyResponse.ok) {
      const body = await keyResponse.json();
      throw new Error(body.detail || "O servidor ainda não foi configurado para enviar notificações.");
    }
    const { chavePublica } = await keyResponse.json();
    const permission = await Notification.requestPermission();
    if (permission !== "granted") {
      throw new Error(permission === "denied"
        ? "As notificações estão bloqueadas nas permissões do navegador."
        : "Permita as notificações para ativar o lembrete.");
    }

    reminderSubscription = await reminderRegistration.pushManager.getSubscription()
      ?? await reminderRegistration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: decodeVapidPublicKey(chavePublica)
      });
    await saveReminderSubscription(reminderSubscription);
    updateReminderControls();
    setReminderStatus("Lembrete ativo neste aparelho para as 22h30.");
  } catch (error) {
    setReminderStatus(error instanceof Error ? error.message : "Não foi possível ativar o lembrete.", true);
  } finally {
    enableReminderButton.disabled = false;
  }
}

async function disableReminder() {
  disableReminderButton.disabled = true;
  setReminderStatus("Desativando o lembrete...");
  try {
    const subscription = reminderSubscription
      ?? await reminderRegistration?.pushManager.getSubscription();
    if (subscription) {
      await removeSavedReminderSubscription(subscription);
      await subscription.unsubscribe();
    }
    reminderSubscription = null;
    updateReminderControls();
    setReminderStatus("Lembrete desativado neste aparelho.");
  } catch (error) {
    setReminderStatus(error instanceof Error ? error.message : "Não foi possível desativar o lembrete.", true);
  } finally {
    disableReminderButton.disabled = false;
  }
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
  initializeReminder();
});
document.querySelector("#name-form").addEventListener("submit", (event) => {
  event.preventDefault();
  participantInput.blur();
});
document.querySelector("#cancel-record").addEventListener("click", () => recordDialog.close());
recordForm.addEventListener("submit", saveRecord);
removeRecordButton.addEventListener("click", deleteRecord);
enableReminderButton.addEventListener("click", enableReminder);
disableReminderButton.addEventListener("click", disableReminder);
window.addEventListener("resize", updateMonthTitle);
participantInput.addEventListener("input", () => {
  setStatus("");
  try {
    localStorage.setItem("devocional.participant", participantInput.value.trim());
  } catch {
    // O cadastro no banco continua funcionando sem persistir esta preferência no navegador.
  }
  renderCalendar();
  if (reminderSubscription) {
    window.clearTimeout(reminderSyncTimer);
    reminderSyncTimer = window.setTimeout(() => {
      const update = participantInput.value.trim()
        ? saveReminderSubscription(reminderSubscription)
        : removeSavedReminderSubscription(reminderSubscription).then(() => {
          setReminderStatus("Informe seu nome para manter o lembrete associado ao calendário.");
        });
      update.catch((error) => {
        setReminderStatus(error instanceof Error ? error.message : "Não foi possível atualizar o lembrete.", true);
      });
    }, 700);
  }
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
  const entries = records[key] || [];
  const people = entries.map((entry) => entry.usuario);
  const currentName = participantInput.value.trim();
  const hasMarked = currentName.length > 0 && entries.some((entry) => entry.usuario.toLocaleLowerCase() === currentName.toLocaleLowerCase());
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
    ? "Seu devocional está registrado. Toque para editar ou remover."
    : "Toque para registrar seu devocional.";
  card.setAttribute(
    "aria-label",
    `${dateLabel}. ${people.length > 0 ? `${people.length} ${people.length === 1 ? "registro" : "registros"}: ${people.join(", ")}. ` : "Sem registros. "}${currentName ? action : "Digite seu nome para registrar."}`
  );
  card.title = entries.length > 0
    ? `${dateLabel}: ${entries.map((entry) => entry.tema ? `${entry.usuario} — ${entry.tema}` : entry.usuario).join("; ")}`
    : `${dateLabel}: sem registros`;
  card.addEventListener("click", () => openRecordDialog(key));

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

  const recordedDays = Object.values(records).filter((entries) => entries.length > 0).length;
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
    const body = await response.json();
    records = body.registros;
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

function openRecordDialog(date) {
  const usuario = participantInput.value.trim();
  selectedDate = date;
  const entries = records[date] || [];
  const existingRecord = entries.find((entry) => entry.usuario.toLocaleLowerCase() === usuario.toLocaleLowerCase());
  const formattedDate = new Date(`${date}T12:00:00`).toLocaleDateString("pt-BR", {
    day: "numeric",
    month: "long",
    year: "numeric"
  });
  dialogTitle.textContent = formattedDate;
  themeInput.value = existingRecord?.tema || "";
  removeRecordButton.hidden = !existingRecord;
  removeRecordButton.disabled = false;
  saveRecordButton.disabled = false;
  dialogStatus.textContent = "";
  dialogStatus.classList.remove("is-error");
  renderDayRecords(entries);
  setStatus("");
  recordDialog.showModal();
  dialogTitle.focus();
}

function renderDayRecords(entries) {
  dayRecords.replaceChildren();
  if (entries.length === 0) {
    dayRecords.textContent = "Ainda não há registros neste dia.";
    return;
  }

  const heading = document.createElement("p");
  heading.className = "day-records-title";
  heading.textContent = "Registros do dia";
  const list = document.createElement("ul");
  for (const entry of entries) {
    const item = document.createElement("li");
    const name = document.createElement("strong");
    name.textContent = entry.usuario;
    item.append(name);
    if (entry.tema) {
      const theme = document.createElement("span");
      theme.textContent = entry.tema;
      item.append(theme);
    } else {
      const noTheme = document.createElement("span");
      noTheme.className = "no-theme";
      noTheme.textContent = "Tema não informado";
      item.append(noTheme);
    }
    list.append(item);
  }
  dayRecords.append(heading, list);
}

async function saveRecord(event) {
  event.preventDefault();
  const usuario = participantInput.value.trim();
  if (!usuario) {
    recordDialog.close();
    setStatus("Digite seu nome antes de registrar um devocional.", true);
    participantInput.focus();
    return;
  }

  isLoading = true;
  setStatus("Salvando seu registro...");
  dialogStatus.textContent = "";
  dialogStatus.classList.remove("is-error");
  renderCalendar();
  saveRecordButton.disabled = true;

  try {
    const response = await fetch("/api/devocional", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ data: selectedDate, usuario, tema: themeInput.value.trim() })
    });
    if (!response.ok) {
      const body = await response.json();
      throw new Error(body.erro || "Não foi possível salvar o registro.");
    }
    recordDialog.close();
    await loadMonth();
  } catch (error) {
    isLoading = false;
    saveRecordButton.disabled = false;
    renderCalendar();
    setStatus("");
    dialogStatus.textContent = error instanceof Error
      ? error.message
      : "Não foi possível salvar o registro. Tente novamente.";
    dialogStatus.classList.add("is-error");
  }
}

async function deleteRecord() {
  const usuario = participantInput.value.trim();
  isLoading = true;
  setStatus("Removendo seu registro...");
  dialogStatus.textContent = "";
  dialogStatus.classList.remove("is-error");
  renderCalendar();
  removeRecordButton.disabled = true;

  try {
    const response = await fetch("/api/devocional", {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ data: selectedDate, usuario })
    });
    if (!response.ok) {
      const body = await response.json();
      throw new Error(body.erro || "Não foi possível remover o registro.");
    }
    recordDialog.close();
    await loadMonth();
  } catch (error) {
    isLoading = false;
    removeRecordButton.disabled = false;
    renderCalendar();
    setStatus("");
    dialogStatus.textContent = error instanceof Error
      ? error.message
      : "Não foi possível remover o registro. Tente novamente.";
    dialogStatus.classList.add("is-error");
  }
}

loadMonth();
