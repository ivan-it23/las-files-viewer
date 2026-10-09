<?php
/**
 * Посредник к языковым моделям polza.ai для «Просмотрщика LAS».
 *
 * Самостоятельный файл: не нужны ни каркас сайта, ни база данных. Кладётся
 * в любую папку сайта, где выполняется PHP (7.4+, с curl).
 *
 * Ключ polza.ai хранится только здесь, на сервере, — в программу он не
 * попадает. Программа предъявляет встроенный ключ клиента (заголовок
 * X-Client-Key); он лишь отсекает случайные обращения, поэтому расход
 * ограничен: запросов в час с одного адреса и общий бюджет в рублях в сутки.
 *
 * Содержимое запросов и ответов не сохраняется: в нём бывают данные скважин
 * заказчиков. В журнал расхода пишутся только время, адрес, модель, число
 * токенов, стоимость и итог.
 *
 * Действия: POST LlmController.php?a=status | models | chat
 */

// ======================================================================
// Настройки — заполните перед загрузкой на хостинг
// ======================================================================

/** Ключ polza.ai — тот же, что в config.php сайта horizon.ru. */
const POLZA_AI_API_KEY = 'ВСТАВЬТЕ_КЛЮЧ_POLZA_AI';

/** Модель по умолчанию — та же, что у ИИ-агента сайта horizon.ru. */
const LLM_DEFAULT_MODEL = 'deepseek/deepseek-v3.2-exp';

/** Ключ клиента, встроенный в «Просмотрщик LAS» (AgentSettings.BuiltInClientKey). Менять вместе с программой. */
const LLM_CLIENT_KEY = 'lasviewer_d9e85fbec598620ac7e99d777aa41f6d';

/** Запросов к модели в час с одного адреса. Один вопрос пользователя — обычно 2–5 запросов. */
const LLM_REQUESTS_PER_HOUR = 300;

/** Общий расход в сутки на всех пользователей, руб. */
const LLM_DAILY_BUDGET_RUB = 1000.0;

/**
 * Разрешённые модели. Пустой список — модель по умолчанию и любые модели
 * каталога polza.ai с поддержкой вызова инструментов.
 */
const LLM_ALLOWED_MODELS = [];

// ======================================================================

const UPSTREAM = 'https://polza.ai/api/v1';
const MAX_BODY_BYTES = 2097152; // 2 МБ
const MAX_MESSAGES = 300;
const MAX_TOOLS = 32;
const MAX_OUTPUT_TOKENS = 8000;
const MODELS_CACHE_SECONDS = 3600;
const STREAM_TIMEOUT_SECONDS = 240;

ini_set('display_errors', '0');
error_reporting(E_ALL);

set_exception_handler(function (Throwable $exception) {
    error_log('[LlmController] ' . $exception->getMessage() . ' @ ' . $exception->getFile() . ':' . $exception->getLine());
    if (!headers_sent()) {
        fail('server_error', 'Внутренняя ошибка посредника. Подробности — в журнале ошибок хостинга.', 500);
    }
});

main();

// ----------------------------------------------------------------------
// Действия
// ----------------------------------------------------------------------

function main(): void
{
    header('X-Content-Type-Options: nosniff');

    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
        fail('bad_method', 'Требуется метод POST.', 405);
    }

    $key = isset($_SERVER['HTTP_X_CLIENT_KEY']) ? trim((string)$_SERVER['HTTP_X_CLIENT_KEY']) : '';
    if ($key === '' || !hash_equals(LLM_CLIENT_KEY, $key)) {
        fail('unauthorized', 'Нет доступа к ИИ-агенту: ключ программы не принят. Обновите «Просмотрщик LAS».', 401);
    }

    $action = isset($_GET['a']) ? (string)$_GET['a'] : '';
    if ($action === 'status') {
        actionStatus();
    } elseif ($action === 'models') {
        actionModels();
    } elseif ($action === 'chat') {
        actionChat();
    } else {
        fail('not_found', 'Неизвестное действие.', 404);
    }
}

function actionStatus(): void
{
    $state = readState();
    $ip = clientIp();
    respond([
        'success' => true,
        'shared'  => true,
        'user'    => ['username' => 'las-viewer', 'full_name' => 'Общий доступ «Просмотрщика LAS»'],
        'service' => 'polza.ai',
        'default_model' => LLM_DEFAULT_MODEL,
        'limits' => [
            'requests_per_hour'  => LLM_REQUESTS_PER_HOUR,
            'requests_last_hour' => requestsThisHour($state, $ip),
            'daily_budget_rub'   => LLM_DAILY_BUDGET_RUB,
            'spent_today_rub'    => round(spentToday($state), 2),
        ],
        'configured'  => isKeyConfigured(),
        'server_time' => date('c'),
    ]);
}

function actionModels(): void
{
    $catalog = toolModels();
    respond([
        'success'           => true,
        'default_model'     => LLM_DEFAULT_MODEL,
        'models'            => $catalog === null ? [['id' => LLM_DEFAULT_MODEL, 'name' => LLM_DEFAULT_MODEL]] : $catalog,
        'catalog_available' => $catalog !== null,
    ]);
}

function actionChat(): void
{
    if (!isKeyConfigured()) {
        fail('not_configured', 'В LlmController.php на сайте не вписан ключ polza.ai.', 503);
    }

    $raw = file_get_contents('php://input', false, null, 0, MAX_BODY_BYTES + 1);
    if ($raw === false || $raw === '') {
        fail('bad_request', 'Пустое тело запроса.', 400);
    }
    if (strlen($raw) > MAX_BODY_BYTES) {
        fail('too_large', 'Запрос больше 2 МБ. Начните новый диалог.', 413);
    }

    $request = json_decode($raw, true);
    if (!is_array($request)) {
        fail('bad_request', 'Тело запроса — не JSON-объект.', 400);
    }

    $payload = buildPayload($request);

    // Лимиты проверяются и счётчик увеличивается под одной блокировкой:
    // два одновременных запроса не проскочат последний свободный слот.
    $ip = clientIp();
    $verdict = updateState(function (array $state) use ($ip) {
        if (spentToday($state) >= LLM_DAILY_BUDGET_RUB) {
            return [$state, 'budget'];
        }
        if (requestsThisHour($state, $ip) >= LLM_REQUESTS_PER_HOUR) {
            return [$state, 'rate'];
        }
        $hour = date('Y-m-d H');
        if (($state['ips'][$ip]['hour'] ?? '') !== $hour) {
            $state['ips'][$ip] = ['hour' => $hour, 'count' => 0];
        }
        $state['ips'][$ip]['count']++;
        return [$state, 'ok'];
    });

    if ($verdict === 'budget') {
        fail('budget_exceeded', 'Исчерпан дневной лимит расходов на модель. Попробуйте завтра.', 429);
    }
    if ($verdict === 'rate') {
        fail('rate_limited', 'Исчерпан часовой лимит запросов к модели. Попробуйте позже.', 429);
    }

    stream($payload, $ip);
    exit;
}

// ----------------------------------------------------------------------
// Запрос к polza.ai
// ----------------------------------------------------------------------

/**
 * Проверяет запрос программы и собирает запрос к сервису. Лишние поля
 * отбрасываются: программа не может подменить адрес или включить у сервиса
 * что-то сверх перечисленного.
 */
function buildPayload(array $request): array
{
    $model = isset($request['model']) && is_string($request['model']) && $request['model'] !== ''
        ? $request['model']
        : LLM_DEFAULT_MODEL;
    if (!isModelAllowed($model)) {
        fail('model_not_allowed', "Модель {$model} не разрешена для программы.", 400);
    }

    $messages = $request['messages'] ?? null;
    if (!is_array($messages) || count($messages) === 0) {
        fail('bad_request', 'Нет сообщений диалога.', 400);
    }
    if (count($messages) > MAX_MESSAGES) {
        fail('too_large', 'Слишком длинный диалог. Начните новый.', 413);
    }
    foreach ($messages as $message) {
        $role = is_array($message) ? ($message['role'] ?? null) : null;
        if (!in_array($role, ['system', 'user', 'assistant', 'tool'], true)) {
            fail('bad_request', 'Недопустимая роль сообщения.', 400);
        }
    }

    $payload = ['model' => $model, 'messages' => array_values($messages), 'stream' => true];

    if (isset($request['tools'])) {
        if (!is_array($request['tools']) || count($request['tools']) > MAX_TOOLS) {
            fail('bad_request', 'Неверный список инструментов.', 400);
        }
        $payload['tools'] = array_values($request['tools']);
        if (isset($request['tool_choice'])) {
            $payload['tool_choice'] = $request['tool_choice'];
        }
    }

    if (isset($request['temperature']) && is_numeric($request['temperature'])) {
        $payload['temperature'] = max(0.0, min(1.5, (float)$request['temperature']));
    }

    $maxTokens = isset($request['max_tokens']) && is_numeric($request['max_tokens']) ? (int)$request['max_tokens'] : 4000;
    $payload['max_tokens'] = max(1, min(MAX_OUTPUT_TOKENS, $maxTokens));

    return $payload;
}

/**
 * Передаёт поток polza.ai программе без изменений и попутно ищет в нём
 * итоговый расход (последнее сообщение перед [DONE] содержит usage).
 */
function stream(array $payload, string $ip): void
{
    set_time_limit(STREAM_TIMEOUT_SECONDS + 30);
    // Обрыв связи с программой («Стоп») обрабатывается здесь: генерация
    // останавливается, а расход записывается.
    ignore_user_abort(true);
    ini_set('output_buffering', '0');
    ini_set('zlib.output_compression', '0');
    if (function_exists('apache_setenv')) {
        apache_setenv('no-gzip', '1');
    }
    while (ob_get_level() > 0) {
        ob_end_clean();
    }

    http_response_code(200);
    header('Content-Type: text/event-stream; charset=utf-8');
    header('Cache-Control: no-cache, no-store, must-revalidate');
    header('X-Accel-Buffering: no');

    $status = 0;
    $errorBody = '';
    $parseBuffer = '';
    $usage = null;
    $aborted = false;

    $ch = curl_init(UPSTREAM . '/chat/completions');
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($payload, JSON_UNESCAPED_UNICODE));
    curl_setopt($ch, CURLOPT_HTTPHEADER, [
        'Content-Type: application/json',
        'Authorization: Bearer ' . POLZA_AI_API_KEY,
        'Accept: text/event-stream',
        // Без этого curl на длинном теле ждёт «100 Continue» и теряет время.
        'Expect:',
    ]);
    curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 15);
    curl_setopt($ch, CURLOPT_TIMEOUT, STREAM_TIMEOUT_SECONDS);
    curl_setopt($ch, CURLOPT_HEADERFUNCTION, function ($ch, $line) use (&$status) {
        if (preg_match('#^HTTP/\S+\s+(\d{3})#', $line, $match)) {
            $status = (int)$match[1];
        }
        return strlen($line);
    });
    curl_setopt($ch, CURLOPT_WRITEFUNCTION, function ($ch, $chunk) use (&$status, &$errorBody, &$parseBuffer, &$usage, &$aborted) {
        if ($status !== 200) {
            if (strlen($errorBody) < 65536) {
                $errorBody .= $chunk;
            }
            return strlen($chunk);
        }

        echo $chunk;
        flush();
        if (connection_aborted()) {
            $aborted = true;
            return 0;
        }

        $parseBuffer .= $chunk;
        while (($pos = strpos($parseBuffer, "\n")) !== false) {
            $line = rtrim(substr($parseBuffer, 0, $pos), "\r");
            $parseBuffer = substr($parseBuffer, $pos + 1);
            if (strncmp($line, 'data:', 5) !== 0) {
                continue;
            }
            $data = ltrim(substr($line, 5));
            if ($data === '[DONE]' || strpos($data, '"usage"') === false) {
                continue;
            }
            $decoded = json_decode($data, true);
            if (is_array($decoded) && isset($decoded['usage']) && is_array($decoded['usage'])) {
                $usage = $decoded['usage'];
            }
        }
        return strlen($chunk);
    });

    curl_exec($ch);
    $curlError = curl_error($ch);
    curl_close($ch);

    $result = 'ok';
    if ($aborted) {
        $result = 'aborted';
    } elseif ($curlError !== '' || $status !== 200) {
        $result = $curlError !== '' ? 'network_error' : 'upstream_error';
        $message = $curlError !== ''
            ? 'Нет связи с polza.ai: ' . $curlError
            : describeUpstreamError($status, $errorBody);
        error_log("[LlmController] polza.ai: status {$status} {$curlError}");
        echo "event: proxy_error\n";
        echo 'data: ' . json_encode(['code' => $result, 'status' => $status, 'message' => $message], JSON_UNESCAPED_UNICODE) . "\n\n";
        flush();
    }

    recordUsage($ip, $payload['model'], $usage, $result);
}

function describeUpstreamError(int $status, string $body): string
{
    $decoded = json_decode($body, true);
    $detail = '';
    if (is_array($decoded)) {
        if (isset($decoded['error']['message']) && is_string($decoded['error']['message'])) {
            $detail = $decoded['error']['message'];
        } elseif (isset($decoded['message']) && is_string($decoded['message'])) {
            $detail = $decoded['message'];
        }
    }

    $prefixes = [
        400 => 'Сервис отклонил запрос',
        401 => 'polza.ai не принял ключ из LlmController.php',
        402 => 'На счёте polza.ai недостаточно средств',
        403 => 'Доступ к модели запрещён',
        404 => 'Модель не найдена',
        429 => 'polza.ai ограничил частоту запросов',
    ];
    $prefix = $prefixes[$status] ?? "polza.ai ответил кодом {$status}";
    if ($detail === '') {
        return $prefix . '.';
    }

    $short = function_exists('mb_substr') ? mb_substr($detail, 0, 500) : substr($detail, 0, 500);
    return "{$prefix}: {$short}";
}

/**
 * Модели каталога polza.ai с поддержкой инструментов; null — каталог недоступен.
 *
 * @return array|null
 */
function toolModels()
{
    if (!isKeyConfigured()) {
        return null;
    }

    $cacheFile = storageDir() . '/models-cache.json';
    if (is_file($cacheFile) && time() - filemtime($cacheFile) < MODELS_CACHE_SECONDS) {
        $cached = json_decode((string)file_get_contents($cacheFile), true);
        if (is_array($cached)) {
            return filterAllowed($cached);
        }
    }

    $ch = curl_init(UPSTREAM . '/models?type=chat');
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_HTTPHEADER, ['Authorization: Bearer ' . POLZA_AI_API_KEY]);
    curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 10);
    curl_setopt($ch, CURLOPT_TIMEOUT, 20);
    $body = curl_exec($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    $decoded = is_string($body) && $status === 200 ? json_decode($body, true) : null;
    if (!is_array($decoded) || !isset($decoded['data']) || !is_array($decoded['data'])) {
        return null;
    }

    $models = [];
    foreach ($decoded['data'] as $model) {
        $provider = is_array($model['top_provider'] ?? null) ? $model['top_provider'] : [];
        $parameters = is_array($provider['supported_parameters'] ?? null) ? $provider['supported_parameters'] : [];
        if (!isset($model['id']) || !in_array('tools', $parameters, true)) {
            continue;
        }
        $pricing = is_array($provider['pricing'] ?? null) ? $provider['pricing'] : [];
        $models[] = [
            'id'                     => (string)$model['id'],
            'name'                   => (string)($model['name'] ?? $model['id']),
            'context_length'         => isset($provider['context_length']) ? (int)$provider['context_length'] : null,
            'prompt_per_million'     => $pricing['prompt_per_million'] ?? null,
            'completion_per_million' => $pricing['completion_per_million'] ?? null,
            'currency'               => $pricing['currency'] ?? 'RUB',
        ];
    }

    usort($models, function ($a, $b) {
        return strcmp($a['name'], $b['name']);
    });
    @file_put_contents($cacheFile, json_encode($models, JSON_UNESCAPED_UNICODE), LOCK_EX);

    return filterAllowed($models);
}

function filterAllowed(array $models): array
{
    if (count(LLM_ALLOWED_MODELS) === 0) {
        return $models;
    }

    return array_values(array_filter($models, function ($model) {
        return in_array($model['id'], LLM_ALLOWED_MODELS, true);
    }));
}

function isModelAllowed(string $model): bool
{
    if ($model === LLM_DEFAULT_MODEL) {
        return true;
    }
    if (count(LLM_ALLOWED_MODELS) > 0) {
        return in_array($model, LLM_ALLOWED_MODELS, true);
    }

    $catalog = toolModels();
    if ($catalog === null) {
        return false;
    }
    foreach ($catalog as $entry) {
        if ($entry['id'] === $model) {
            return true;
        }
    }

    return false;
}

function isKeyConfigured(): bool
{
    return POLZA_AI_API_KEY !== '' && strpos(POLZA_AI_API_KEY, 'ВСТАВЬТЕ') === false;
}

// ----------------------------------------------------------------------
// Лимиты и журнал расхода — файлы вне папки сайта, если она доступна
// ----------------------------------------------------------------------

/**
 * Каталог для счётчиков и журнала. Сначала — на два уровня выше этого файла
 * (для …/site/public_html/папка/ это …/site/, вне доступа из интернета),
 * иначе — временный каталог PHP.
 */
function storageDir(): string
{
    static $dir = null;
    if ($dir !== null) {
        return $dir;
    }

    $candidates = [dirname(__DIR__, 2) . '/llm-proxy-data', sys_get_temp_dir() . '/llm-proxy-data'];
    foreach ($candidates as $candidate) {
        if (is_dir($candidate) || @mkdir($candidate, 0750, true)) {
            if (is_writable($candidate)) {
                $dir = $candidate;
                return $dir;
            }
        }
    }

    $dir = sys_get_temp_dir();
    return $dir;
}

function readState(): array
{
    $file = storageDir() . '/state.json';
    $state = is_file($file) ? json_decode((string)file_get_contents($file), true) : null;
    return is_array($state) ? $state : [];
}

/**
 * Изменяет состояние под блокировкой файла.
 *
 * @param callable $change function(array $state): [array $state, mixed $result]
 * @return mixed
 */
function updateState(callable $change)
{
    $handle = fopen(storageDir() . '/state.json', 'c+');
    if ($handle === false) {
        // Без счётчиков посредник всё равно работает — лимиты не применяются.
        error_log('[LlmController] нет доступа к файлу счётчиков');
        return 'ok';
    }

    flock($handle, LOCK_EX);
    $text = stream_get_contents($handle);
    $state = $text !== false && $text !== '' ? json_decode($text, true) : null;
    if (!is_array($state)) {
        $state = [];
    }

    // Сутки сменились — расход и адреса начинаются заново.
    if (($state['day'] ?? '') !== date('Y-m-d')) {
        $state = ['day' => date('Y-m-d'), 'spent' => 0.0, 'ips' => []];
    }

    list($state, $result) = $change($state);

    ftruncate($handle, 0);
    rewind($handle);
    fwrite($handle, json_encode($state));
    fflush($handle);
    flock($handle, LOCK_UN);
    fclose($handle);

    return $result;
}

function requestsThisHour(array $state, string $ip): int
{
    $entry = $state['ips'][$ip] ?? null;
    return is_array($entry) && ($entry['hour'] ?? '') === date('Y-m-d H') ? (int)$entry['count'] : 0;
}

function spentToday(array $state): float
{
    return ($state['day'] ?? '') === date('Y-m-d') ? (float)($state['spent'] ?? 0) : 0.0;
}

/** @param array|null $usage */
function recordUsage(string $ip, string $model, $usage, string $result): void
{
    $prompt = (int)($usage['prompt_tokens'] ?? 0);
    $completion = (int)($usage['completion_tokens'] ?? 0);
    $cost = (float)($usage['cost_rub'] ?? $usage['cost'] ?? 0);

    updateState(function (array $state) use ($cost) {
        $state['spent'] = (float)($state['spent'] ?? 0) + $cost;
        return [$state, null];
    });

    $line = implode(';', [date('Y-m-d H:i:s'), $ip, $model, $result, $prompt, $completion, number_format($cost, 4, '.', '')]) . "\n";
    @file_put_contents(storageDir() . '/usage-' . date('Y-m') . '.csv', $line, FILE_APPEND | LOCK_EX);
}

// ----------------------------------------------------------------------
// Ответы
// ----------------------------------------------------------------------

function clientIp(): string
{
    return substr((string)($_SERVER['REMOTE_ADDR'] ?? 'unknown'), 0, 45);
}

function respond(array $data, int $code = 200): void
{
    http_response_code($code);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode($data, JSON_UNESCAPED_UNICODE);
    exit;
}

function fail(string $code, string $message, int $httpCode): void
{
    respond(['success' => false, 'code' => $code, 'message' => $message], $httpCode);
}
