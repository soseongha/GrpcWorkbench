let currentSessionId = null;
let currentProtoServices = [];
let currentProtoContent = null;
let currentSelectedService = null;
let currentSelectedMethod = null;
let isStreaming = false;
let healthCheckInterval = null;
let streamingAbortController = null;
let streamingSentCount = 0;
let hubConnection = null;
let currentStreamId = null;

// ============ 로깅 유틸리티 ============
function addLog(message, type = 'info') {
    const logContainer = document.getElementById('logContainer');
    if (!logContainer) return;

    const timestamp = new Date().toLocaleTimeString('ko-KR');
    const logEntry = document.createElement('div');
    logEntry.className = `text-${type === 'error' ? 'danger' : type === 'success' ? 'success' : 'muted'}`;
    logEntry.textContent = `[${timestamp}] ${message}`;
    logContainer.appendChild(logEntry);
    logContainer.scrollTop = logContainer.scrollHeight;
}

function clearLogs() {
    const logContainer = document.getElementById('logContainer');
    if (logContainer) logContainer.innerHTML = '';
    addLog('로그가 초기화되었습니다.');
}

async function extractErrorMessage(response, fallbackMessage) {
    try {
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            const errData = await response.json();
            if (errData?.error) return errData.error;
            if (errData?.message) return errData.message;
            return fallbackMessage;
        }

        const text = await response.text();
        return text || fallbackMessage;
    } catch {
        return fallbackMessage;
    }
}

// ============ Step 1: 세션 생성 ============
async function createSession() {
    addLog('세션 생성 중... (UDS 설정값 사용)');

    try {
        const response = await fetch('/api/proto/create-session', {
            method: 'POST'
        });

        if (!response.ok) {
            const errorMessage = await extractErrorMessage(response, '세션 생성 실패');
            throw new Error(errorMessage);
        }

        const data = await response.json();
        currentSessionId = data.sessionId;
        localStorage.setItem('grpcSessionId', currentSessionId);

        const sessionStatusEl = document.getElementById('sessionStatus');
        const sessionStatusTextEl = document.getElementById('sessionStatusText');
        if (sessionStatusEl) sessionStatusEl.classList.remove('d-none');
        if (sessionStatusTextEl) {
            sessionStatusTextEl.textContent = `${currentSessionId.substring(0, 8)}... (UDS)`;
        }

        const uploadBtnEl = document.getElementById('uploadBtn');
        if (uploadBtnEl) uploadBtnEl.disabled = false;

        addLog(`✓ 세션 생성 완료 (UDS): ${currentSessionId} / ${data.unixSocketPath || 'N/A'}`, 'success');
        startHealthCheck();
    } catch (error) {
        addLog(`세션 생성 실패: ${error.message}`, 'error');
    }
}

// ============ Step 2: Proto 파일 업로드 ============
async function uploadProto() {
    if (!currentSessionId) {
        addLog('먼저 세션을 생성하세요.', 'error');
        return;
    }

    const fileInput = document.getElementById('protoFile');
    if (!fileInput || !fileInput.files.length) {
        addLog('Proto 파일을 선택하세요.', 'error');
        return;
    }

    const file = fileInput.files[0];
    const formData = new FormData();
    formData.append('sessionId', currentSessionId);
    formData.append('protoFile', file);

    try {
        addLog(`파일 업로드 중... (${file.name})`);
        const response = await fetch('/api/proto/upload', {
            method: 'POST',
            body: formData
        });

        if (!response.ok) {
            const errorMessage = await extractErrorMessage(response, `Proto 업로드 실패 (${response.status})`);
            throw new Error(errorMessage);
        }

        const data = await response.json();
        currentProtoServices = data.services || [];

        // proto 내용 저장
        try {
            currentProtoContent = await file.text();
        } catch (readErr) {
            // 무시
        }

        onProtoLoaded();
        addLog(`✓ Proto 파일 로드 완료: ${currentProtoServices.length}개 서비스`, 'success');
    } catch (error) {
        addLog(`파일 업로드 실패: ${error.message}`, 'error');
    }
}

// Proto 파일 전문 보기
function showProtoFile() {
    if (!currentProtoContent) {
        addLog('Proto 파일이 로드되지 않았습니다.', 'error');
        return;
    }

    const protoFileContent = document.getElementById('protoFileContent');
    const protoFileText = document.getElementById('protoFileText');
    if (protoFileContent && protoFileText) {
        protoFileText.textContent = currentProtoContent;
        protoFileContent.classList.remove('d-none');
    }
}

function hideProtoFile() {
    const el = document.getElementById('protoFileContent');
    if (el) el.classList.add('d-none');
}

// Proto 텍스트 직접 업로드
async function uploadProtoText() {
    if (!currentSessionId) {
        addLog('먼저 세션을 생성하세요.', 'error');
        return;
    }

    const protoText = document.getElementById('protoEditor').value.trim();
    if (!protoText) {
        addLog('Proto 정의를 입력하세요.', 'error');
        return;
    }

    try {
        addLog('Proto 정의 적용 중...');
        const response = await fetch('/api/proto/upload-text', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                sessionId: currentSessionId,
                protoText
            })
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({}));
            throw new Error(err.error || 'Proto 적용 실패');
        }

        const data = await response.json();
        currentProtoServices = data.services || [];
        currentProtoContent = protoText;

        onProtoLoaded();
        addLog(`✓ Proto 정의 적용 완료: ${currentProtoServices.length}개 서비스`, 'success');
    } catch (error) {
        addLog(`Proto 적용 실패: ${error.message}`, 'error');
    }
}

function onProtoLoaded() {
    const uploadStatusEl = document.getElementById('uploadStatus');
    const uploadStatusTextEl = document.getElementById('uploadStatusText');
    const sendBtnEl = document.getElementById('sendBtn');
    const viewProtoBtnEl = document.getElementById('viewProtoBtn');

    if (uploadStatusEl) uploadStatusEl.classList.remove('d-none');
    if (uploadStatusTextEl) uploadStatusTextEl.textContent = `${currentProtoServices.length}개 서비스 로드됨`;
    if (sendBtnEl) sendBtnEl.disabled = false;
    if (viewProtoBtnEl) viewProtoBtnEl.disabled = false;
    populateServiceSelect();
}

// ============ Step 3: 서비스 및 메서드 선택 ============
function populateServiceSelect() {
    const serviceSelect = document.getElementById('serviceSelect');
    serviceSelect.innerHTML = '<option value="">-- 서비스 선택 --</option>';

    currentProtoServices.forEach((service, index) => {
        const option = document.createElement('option');
        option.value = index;
        option.textContent = service.serviceName;
        serviceSelect.appendChild(option);
    });
}

function onServiceChanged() {
    const serviceSelect = document.getElementById('serviceSelect');
    const methodSelect = document.getElementById('methodSelect');
    const selectedIndex = serviceSelect.value;

    methodSelect.innerHTML = '<option value="">-- 메서드 선택 --</option>';
    document.getElementById('methodInfo').classList.add('d-none');

    if (selectedIndex === '') {
        currentSelectedService = null;
        currentSelectedMethod = null;
        return;
    }

    currentSelectedService = currentProtoServices[selectedIndex];

    if (currentSelectedService && currentSelectedService.methods) {
        currentSelectedService.methods.forEach((method, index) => {
            const option = document.createElement('option');
            option.value = index;
            option.textContent = method.methodName;
            methodSelect.appendChild(option);
        });
    }
}

function onMethodChanged() {
    const methodSelect = document.getElementById('methodSelect');
    const methodInfo = document.getElementById('methodInfo');
    const methodInfoText = document.getElementById('methodInfoText');
    const selectedIndex = methodSelect.value;

    if (selectedIndex === '' || !currentSelectedService) {
        currentSelectedMethod = null;
        methodInfo.classList.add('d-none');
        document.getElementById('streamingSettings').classList.add('d-none');
        return;
    }

    currentSelectedMethod = currentSelectedService.methods[selectedIndex];

    if (currentSelectedMethod) {
        isStreaming = currentSelectedMethod.rpcType !== 'Unary';

        const typeMap = {
            'Unary': { label: '단일 요청/응답', color: 'bg-success' },
            'ServerStreaming': { label: '서버 스트리밍', color: 'bg-info' },
            'ClientStreaming': { label: '클라이언트 스트리밍', color: 'bg-warning' },
            'BidirectionalStreaming': { label: '양방향 스트리밍', color: 'bg-danger' }
        };

        const typeInfo = typeMap[currentSelectedMethod.rpcType] || { label: currentSelectedMethod.rpcType, color: 'bg-secondary' };

        methodInfoText.innerHTML = `
            <strong>메서드:</strong> ${currentSelectedMethod.methodName}
            <span class="badge ${typeInfo.color} ms-2">${typeInfo.label}</span><br>
            <strong>입력:</strong> ${currentSelectedMethod.inputType} |
            <strong>출력:</strong> ${currentSelectedMethod.outputType}
        `;
        methodInfo.classList.remove('d-none');

        // 스트리밍 설정 표시/숨김 + 버튼 전환
        const streamingSettings = document.getElementById('streamingSettings');
        const unaryButtons = document.getElementById('unaryButtons');
        const streamingButtons = document.getElementById('streamingButtons');
        const isClientStream = currentSelectedMethod.rpcType === 'ClientStreaming' || currentSelectedMethod.rpcType === 'BidirectionalStreaming';

        if (isClientStream) {
            if (streamingSettings) streamingSettings.classList.remove('d-none');
            if (unaryButtons) unaryButtons.classList.add('d-none');
            if (streamingButtons) streamingButtons.classList.remove('d-none');
            const startBtn = document.getElementById('startStreamBtn');
            if (startBtn) startBtn.disabled = false;
        } else {
            if (streamingSettings) streamingSettings.classList.add('d-none');
            if (unaryButtons) unaryButtons.classList.remove('d-none');
            if (streamingButtons) streamingButtons.classList.add('d-none');
        }

        // 진행바 초기화
        const progressEl = document.getElementById('streamProgress');
        if (progressEl) progressEl.classList.add('d-none');

        showMessageDefinitions(currentSelectedMethod);
        createRequestForm(currentSelectedMethod);
    }
}

// ============ 메타데이터 입력 ============
function addMetadataRow() {
    const container = document.getElementById('metadataContainer');
    const row = document.createElement('div');
    row.className = 'input-group input-group-sm mb-1';
    row.innerHTML = `
        <input type="text" class="form-control metadata-key" placeholder="key">
        <input type="text" class="form-control metadata-value" placeholder="value">
        <button class="btn btn-outline-danger" type="button" onclick="this.parentElement.remove()">✕</button>
    `;
    container.appendChild(row);
}

function getMetadata() {
    const metadata = {};
    const keys = document.querySelectorAll('.metadata-key');
    const values = document.querySelectorAll('.metadata-value');
    keys.forEach((key, i) => {
        if (key.value.trim() && values[i]?.value.trim()) {
            metadata[key.value.trim()] = values[i].value.trim();
        }
    });
    return Object.keys(metadata).length > 0 ? metadata : null;
}

// ============ 메시지 정의 표시 ============
function showMessageDefinitions(method) {
    const messageDefinitions = document.getElementById('messageDefinitions');
    const requestMessageDef = document.getElementById('requestMessageDef');
    const responseMessageDef = document.getElementById('responseMessageDef');

    if (!currentProtoContent) {
        messageDefinitions.classList.add('d-none');
        return;
    }

    const inputTypeName = method.inputType.split('.').pop();
    const outputTypeName = method.outputType.split('.').pop();

    const requestContent = extractMessageDefinition(currentProtoContent, inputTypeName);
    const responseContent = extractMessageDefinition(currentProtoContent, outputTypeName);

    if (requestContent) {
        document.getElementById('requestMessageName').textContent = inputTypeName;
        document.getElementById('requestMessageContent').textContent = requestContent;
        requestMessageDef.classList.remove('d-none');
    } else {
        requestMessageDef.classList.add('d-none');
    }

    if (responseContent) {
        document.getElementById('responseMessageName').textContent = outputTypeName;
        document.getElementById('responseMessageContent').textContent = responseContent;
        responseMessageDef.classList.remove('d-none');
    } else {
        responseMessageDef.classList.add('d-none');
    }

    messageDefinitions.classList.remove('d-none');
}

function extractMessageDefinition(protoContent, messageName) {
    const regex = new RegExp(`message\\s+${messageName}\\s*\\{`, 's');
    const match = protoContent.search(regex);

    if (match !== -1) {
        const start = protoContent.indexOf('{', match);
        let braceCount = 0;
        let end = start;

        for (let i = start; i < protoContent.length; i++) {
            if (protoContent[i] === '{') braceCount++;
            if (protoContent[i] === '}') braceCount--;
            if (braceCount === 0) { end = i + 1; break; }
        }
        return protoContent.substring(match, end);
    }
    return null;
}

// ============ 동적 요청 폼 생성 ============
function createRequestForm(method) {
    const requestFormContainer = document.getElementById('requestFormContainer');
    const requestPlaceholder = document.getElementById('requestPlaceholder');
    const requestForm = document.getElementById('requestForm');

    if (!method.inputSchema) {
        requestFormContainer.classList.add('d-none');
        requestPlaceholder.classList.remove('d-none');
        return;
    }

    try {
        const schema = JSON.parse(method.inputSchema);
        const properties = schema.properties || {};

        requestForm.innerHTML = '';

        Object.entries(properties).forEach(([fieldName, fieldInfo]) => {
            const formGroup = document.createElement('div');
            formGroup.className = 'form-group mb-3';

            const label = document.createElement('label');
            label.htmlFor = `field_${fieldName}`;
            label.className = 'form-label small';
            label.textContent = `${fieldName} (${fieldInfo.type})`;

            let input;
            if (fieldInfo.type === 'integer') {
                input = document.createElement('input');
                input.type = 'number';
            } else if (fieldInfo.type === 'boolean') {
                input = document.createElement('select');
                input.innerHTML = '<option value="false">false</option><option value="true">true</option>';
            } else {
                input = document.createElement('input');
                input.type = 'text';
            }

            input.id = `field_${fieldName}`;
            input.className = 'form-control form-control-sm';
            input.placeholder = `${fieldName} 입력`;
            input.dataset.fieldName = fieldName;
            input.dataset.fieldType = fieldInfo.type;

            formGroup.appendChild(label);
            formGroup.appendChild(input);
            requestForm.appendChild(formGroup);
        });

        requestFormContainer.classList.remove('d-none');
        requestPlaceholder.classList.add('d-none');
    } catch (e) {
        requestFormContainer.classList.add('d-none');
        requestPlaceholder.classList.remove('d-none');
    }
}

function buildRequestJson() {
    const form = document.getElementById('requestForm');
    const inputs = form.querySelectorAll('input, select');
    const jsonObj = {};

    inputs.forEach(input => {
        const fieldName = input.dataset.fieldName;
        const fieldType = input.dataset.fieldType;
        let value = input.value;

        if (fieldType === 'integer') {
            if (value !== '') jsonObj[fieldName] = parseInt(value);
        } else if (fieldType === 'boolean') {
            jsonObj[fieldName] = value === 'true';
        } else if (value !== '') {
            jsonObj[fieldName] = value;
        }
    });

    return JSON.stringify(jsonObj);
}

// ============ Step 4: 요청 전송 ============
async function sendRequest() {
    if (!currentSessionId || !currentSelectedMethod) {
        addLog('세션 및 메서드를 선택하세요.', 'error');
        return;
    }

    const payload = buildRequestJson();
    if (!payload || payload === '{}') {
        addLog('요청 데이터를 입력하세요.', 'error');
        return;
    }

    const timeoutSeconds = parseInt(document.getElementById('timeoutSeconds')?.value) || 30;
    const metadata = getMetadata();

    try {
        addLog(`요청 전송 중... (${currentSelectedMethod.methodName})`);

        const requestData = {
            sessionId: currentSessionId,
            serviceName: currentSelectedService.serviceName,
            methodName: currentSelectedMethod.methodName,
            requestJson: payload,
            timeoutSeconds,
            metadata
        };

        let endpoint = '/api/unary/call';
        let requestBody = requestData;

        if (currentSelectedMethod.rpcType === 'ServerStreaming') {
            endpoint = '/api/streaming/server-streaming';
        }

        const response = await fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(requestBody)
        });

        if (!response.ok) {
            const errorData = await response.json().catch(() => ({}));
            throw new Error(errorData.error || '요청 실패');
        }

        const data = await response.json();
        console.log('응답 데이터:', data);
        addLog(`📡 서버 응답 수신: isSuccess=${data.isSuccess}, elapsedMs=${data.elapsedMilliseconds}`);

        if (data.isSuccess) {
            if (data.messages && Array.isArray(data.messages)) {
                addLog(`📨 스트리밍 응답 ${data.messages.length}개 메시지 수신`, 'success');
                displayStreamingResponse(data.messages);
            } else if (data.responseJson) {
                addLog(`📨 Unary 응답 수신 (${data.responseJson.length} chars)`, 'success');
                displayResponse(data);
            } else {
                addLog(`⚠ 응답 데이터가 비어있습니다: ${JSON.stringify(data)}`, 'error');
                displayError('응답 데이터가 비어있습니다.');
            }
        } else {
            addLog(`❌ 서버에서 에러 반환: ${data.errorMessage}`, 'error');
            throw new Error(data.errorMessage || '요청 실패');
        }

        addLog(`✓ 요청 완료`, 'success');
    } catch (error) {
        addLog(`❌ 요청 실패: ${error.message}`, 'error');
        displayError(error.message);
    }
}

// ============ SignalR 연결 ============
async function ensureHubConnection() {
    if (hubConnection && hubConnection.state === signalR.HubConnectionState.Connected) {
        return hubConnection;
    }

    hubConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/grpc-workbench')
        .withAutomaticReconnect()
        .build();

    // 스트림 열림
    hubConnection.on('StreamOpened', (streamId) => {
        currentStreamId = streamId;
        addLog(`✓ 스트림 열림: ${streamId}`, 'success');
    });

    // 메시지 전송 확인
    hubConnection.on('StreamMessageSent', (streamId) => {
        // 진행바는 startStreaming 루프에서 업데이트
    });

    // 스트림 닫힘 + 응답
    hubConnection.on('StreamClosed', (data) => {
        if (data.response) {
            displayStreamingResponse([data.response]);
        } else if (data.messages && data.messages.length > 0) {
            displayStreamingResponse(data.messages);
        }
        addLog(`✓ 스트림 종료 완료`, 'success');
        currentStreamId = null;
    });

    hubConnection.on('StreamingError', (error) => {
        addLog(`❌ 스트리밍 에러: ${error}`, 'error');
        displayError(error);
        currentStreamId = null;
    });

    await hubConnection.start();
    addLog('SignalR 연결 완료');
    return hubConnection;
}

// ============ 스트리밍 제어 (진짜 스트리밍) ============
async function startStreaming() {
    if (!currentSessionId || !currentSelectedMethod) {
        addLog('세션 및 메서드를 선택하세요.', 'error');
        return;
    }

    const payload = buildRequestJson();
    if (!payload || payload === '{}') {
        addLog('요청 데이터를 입력하세요.', 'error');
        return;
    }

    const streamCount = parseInt(document.getElementById('streamCount')?.value) || 5;
    const streamInterval = parseInt(document.getElementById('streamInterval')?.value) || 500;
    const timeoutSeconds = parseInt(document.getElementById('timeoutSeconds')?.value) || 30;
    const metadata = getMetadata();

    // UI 상태 전환
    const startBtn = document.getElementById('startStreamBtn');
    const stopBtn = document.getElementById('stopStreamBtn');
    if (startBtn) startBtn.disabled = true;
    if (stopBtn) stopBtn.disabled = false;

    streamingSentCount = 0;
    streamingAbortController = new AbortController();

    // 진행바 표시
    const progressEl = document.getElementById('streamProgress');
    const progressBar = document.getElementById('streamProgressBar');
    const progressText = document.getElementById('streamProgressText');
    if (progressEl) progressEl.classList.remove('d-none');
    if (progressBar) progressBar.style.width = '0%';
    if (progressText) progressText.textContent = `0 / ${streamCount}`;

    try {
        // 1) SignalR 연결
        const conn = await ensureHubConnection();

        // 2) 스트림 열기
        addLog(`▶ 스트림 열기 중...`);
        const openPayload = {
            sessionId: currentSessionId,
            serviceName: currentSelectedService.serviceName,
            methodName: currentSelectedMethod.methodName,
            requestJson: payload,
            timeoutSeconds,
            metadata
        };

        // StreamOpened 이벤트를 기다림
        const streamIdPromise = new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('스트림 열기 타임아웃')), 10000);

            const onOpened = (streamId) => {
                clearTimeout(timeout);
                hubConnection.off('StreamOpened', onOpened);
                hubConnection.off('StreamingError', onError);
                resolve(streamId);
            };
            const onError = (err) => {
                clearTimeout(timeout);
                hubConnection.off('StreamOpened', onOpened);
                hubConnection.off('StreamingError', onError);
                reject(new Error(err));
            };

            hubConnection.on('StreamOpened', onOpened);
            hubConnection.on('StreamingError', onError);
        });

        await conn.invoke('OpenStream', openPayload);
        const streamId = await streamIdPromise;
        currentStreamId = streamId;

        addLog(`▶ 스트리밍 시작: ${streamCount}건, ${streamInterval}ms 간격`, 'success');

        // 3) 간격마다 메시지 1건씩 전송
        for (let i = 0; i < streamCount; i++) {
            if (streamingAbortController.signal.aborted) {
                addLog(`⏹ 스트리밍 중단됨 (${streamingSentCount}/${streamCount}건 전송)`, 'info');
                break;
            }

            await conn.invoke('SendStreamMessage', streamId, payload);
            streamingSentCount = i + 1;

            // 진행바 업데이트
            const pct = Math.round((streamingSentCount / streamCount) * 100);
            if (progressBar) progressBar.style.width = `${pct}%`;
            if (progressText) progressText.textContent = `${streamingSentCount} / ${streamCount}`;

            addLog(`📤 메시지 ${streamingSentCount}/${streamCount} 전송 완료`);

            // 마지막이 아니면 간격 대기
            if (i < streamCount - 1 && !streamingAbortController.signal.aborted) {
                await new Promise((resolve) => {
                    const timer = setTimeout(resolve, streamInterval);
                    streamingAbortController.signal.addEventListener('abort', () => {
                        clearTimeout(timer);
                        resolve();
                    }, { once: true });
                });
            }
        }

        // 4) 스트림 닫기 + 응답 대기
        addLog(`📡 스트림 닫기 중... (${streamingSentCount}건 전송됨)`);

        const closePromise = new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('스트림 닫기 타임아웃')), 30000);

            const onClosed = (data) => {
                clearTimeout(timeout);
                hubConnection.off('StreamClosed', onClosed);
                hubConnection.off('StreamingError', onCloseError);
                resolve(data);
            };
            const onCloseError = (err) => {
                clearTimeout(timeout);
                hubConnection.off('StreamClosed', onClosed);
                hubConnection.off('StreamingError', onCloseError);
                reject(new Error(err));
            };

            hubConnection.on('StreamClosed', onClosed);
            hubConnection.on('StreamingError', onCloseError);
        });

        await conn.invoke('CloseStream', streamId);
        const result = await closePromise;

        if (result.response) {
            displayStreamingResponse([result.response]);
        } else if (result.messages && result.messages.length > 0) {
            displayStreamingResponse(result.messages);
        }

        addLog(`✓ 스트리밍 완료: ${streamingSentCount}건 전송`, 'success');

    } catch (error) {
        if (error.name !== 'AbortError') {
            addLog(`❌ 스트리밍 실패: ${error.message}`, 'error');
            displayError(error.message);
        }

        // 에러 시 스트림 정리 시도
        if (currentStreamId && hubConnection) {
            try { await hubConnection.invoke('CloseStream', currentStreamId); } catch {}
        }
    } finally {
        // UI 복원
        if (startBtn) startBtn.disabled = false;
        if (stopBtn) stopBtn.disabled = true;
        streamingAbortController = null;
        currentStreamId = null;
    }
}

function stopStreaming() {
    if (streamingAbortController) {
        streamingAbortController.abort();
        addLog(`⏹ 스트리밍 종료 요청`, 'info');
    }
}

// ============ 응답 표시 ============
function displayResponse(data) {
    const container = document.getElementById('responseContainer');
    console.log('displayResponse 호출됨:', data);
    addLog(`UI 업데이트 중... (isSuccess=${data.isSuccess})`);

    if (data.isSuccess) {
        try {
            const parsed = JSON.parse(data.responseJson);
            container.innerHTML = `
                <div class="alert alert-success mb-2">요청이 성공했습니다. (${data.elapsedMilliseconds}ms)</div>
                <pre class="bg-light p-3 rounded"><code>${JSON.stringify(parsed, null, 2)}</code></pre>
            `;
            addLog(`✓ 응답 UI 업데이트 완료`, 'success');
        } catch {
            container.innerHTML = `
                <div class="alert alert-success mb-2">요청이 성공했습니다. (${data.elapsedMilliseconds}ms)</div>
                <pre class="bg-light p-3 rounded"><code>${data.responseJson}</code></pre>
            `;
            addLog(`✓ 응답 UI 업데이트 완료 (파싱 불가)`, 'success');
        }
    } else {
        container.innerHTML = `
            <div class="alert alert-danger mb-2">요청이 실패했습니다.</div>
            <pre class="bg-light p-3 rounded"><code>${data.errorMessage}</code></pre>
        `;
        addLog(`❌ 에러 UI 업데이트 완료`, 'error');
    }

    const copyBtn = document.getElementById('copyBtn');
    if (copyBtn) copyBtn.disabled = false;
}

function displayStreamingResponse(messages) {
    const container = document.getElementById('responseContainer');

    try {
        const parsed = messages.map(msg => {
            try { return JSON.parse(msg); } catch { return msg; }
        });

        parsed.forEach((msg, i) => {
            const str = typeof msg === 'string' ? msg : JSON.stringify(msg);
            addLog(`📨 메시지 ${i + 1}: ${str.substring(0, 80)}...`, 'success');
        });

        container.innerHTML = `
            <div class="alert alert-success mb-2">${parsed.length}개 메시지 수신 완료</div>
            <pre class="bg-light p-3 rounded"><code>${JSON.stringify(parsed, null, 2)}</code></pre>
        `;
    } catch (e) {
        container.innerHTML = `
            <div class="alert alert-danger mb-2">응답 처리 중 오류 발생</div>
            <pre class="bg-light p-3 rounded"><code>${JSON.stringify(messages, null, 2)}</code></pre>
        `;
    }

    const copyBtn2 = document.getElementById('copyBtn');
    if (copyBtn2) copyBtn2.disabled = false;
}

function displayError(message) {
    document.getElementById('responseContainer').innerHTML = `<div class="alert alert-danger">${message}</div>`;
}

function copyResponse() {
    const codeElement = document.querySelector('#responseContainer code');
    if (codeElement) {
        navigator.clipboard.writeText(codeElement.textContent).then(() => {
            addLog('응답이 클립보드에 복사되었습니다.', 'success');
        });
    }
}

// ============ 페이지 초기화 ============
window.addEventListener('load', () => {
    // 이벤트 리스너 등록
    initializeEventListeners();

    const savedSessionId = localStorage.getItem('grpcSessionId');
    if (savedSessionId) {
        currentSessionId = savedSessionId;
        const sessionStatusEl = document.getElementById('sessionStatus');
        const sessionStatusTextEl = document.getElementById('sessionStatusText');
        const uploadBtnEl = document.getElementById('uploadBtn');

        if (sessionStatusEl) sessionStatusEl.classList.remove('d-none');
        if (sessionStatusTextEl) sessionStatusTextEl.textContent = `${savedSessionId.substring(0, 8)}...`;
        if (uploadBtnEl) uploadBtnEl.disabled = false;

        startHealthCheck();
    }

    addLog('GrpcWorkbench 준비 완료. 세션을 생성하세요.');
});

// ============ 서버 상태 확인 ============
function startHealthCheck() {
    if (healthCheckInterval) clearInterval(healthCheckInterval);
    checkServerHealth();
    healthCheckInterval = setInterval(checkServerHealth, 5000);
}

async function checkServerHealth() {
    if (!currentSessionId) {
        updateHealthUI('unknown');
        return;
    }

    try {
        const response = await fetch(`/api/proto/health-check/${currentSessionId}`, {
            signal: AbortSignal.timeout(5000)
        });

        if (response.status === 404) {
            // 서버에 세션이 없음 (서버 재시작 등)
            currentSessionId = null;
            localStorage.removeItem('grpcSessionId');
            updateHealthUI('disconnected');
            addLog('⚠ 세션이 만료되었습니다. 세션을 다시 생성하세요.', 'error');
            if (healthCheckInterval) { clearInterval(healthCheckInterval); healthCheckInterval = null; }
            return;
        }

        if (!response.ok) { updateHealthUI('disconnected'); return; }

        const data = await response.json();
        updateHealthUI(data.status);
    } catch {
        updateHealthUI('disconnected');
    }
}

function updateHealthUI(status) {
    const dot = document.getElementById('healthDot');
    const text = document.getElementById('healthText');
    if (!dot || !text) return;

    const states = {
        'connected': { color: '#198754', label: '서버 연결됨', textColor: '#146c43' },
        'disconnected': { color: '#dc3545', label: '서버 끊김', textColor: '#b02a37' },
        'unknown': { color: '#6c757d', label: '확인 중...', textColor: '#495057' }
    };

    const state = states[status] || states['unknown'];
    dot.style.backgroundColor = state.color;
    text.textContent = state.label;
    text.style.color = state.textColor;
}

// ============ 이벤트 리스너 등록 ============
function initializeEventListeners() {
}
