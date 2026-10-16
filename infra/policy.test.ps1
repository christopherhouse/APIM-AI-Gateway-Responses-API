#requires -Version 7

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

[xml]$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'policies\responses.xml') -Raw

function Assert-Contract($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$validation = $policy.SelectSingleNode('//inbound/set-variable[@name="labValidationError"]').value
$member = $policy.SelectSingleNode('//outbound/set-variable[@name="labMember"]').value
$breaker = $policy.SelectSingleNode('/policies/on-error/choose/when[set-header[@name="X-Lab-Error-Reason"]]').condition
$source = @'
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public class PolicyHeaders : Dictionary<string, string[]> {
    public PolicyHeaders() : base(StringComparer.OrdinalIgnoreCase) {}
    public string GetValueOrDefault(string name, string fallback) {
        return TryGetValue(name, out var values) ? string.Join(",", values) : fallback;
    }
}
public class PolicyBody {
    public string Value;
    public bool Preserved;
    public T As<T>(bool preserveContent) {
        Preserved = preserveContent;
        return (T)(object)Value;
    }
}
public class PolicyUrl {
    public Dictionary<string, string[]> Query = new Dictionary<string, string[]>();
    public string Host;
}
public class PolicyRequest {
    public PolicyHeaders Headers = new PolicyHeaders();
    public PolicyBody Body = new PolicyBody();
    public PolicyUrl Url = new PolicyUrl();
}
public class PolicyVariables : Dictionary<string, object> {
    public T GetValueOrDefault<T>(string name, T fallback) {
        return TryGetValue(name, out var value) ? (T)value : fallback;
    }
}
public class PolicyResponse { public int StatusCode = 200; }
public class PolicyError { public string Reason = "CircuitBreakerIsOpen"; }
public class PolicyContext {
    public PolicyRequest Request = new PolicyRequest();
    public PolicyVariables Variables = new PolicyVariables();
    public Guid RequestId = Guid.Empty;
    public PolicyResponse Response = new PolicyResponse();
    public PolicyError LastError = new PolicyError();
}
public static class PolicyExpressions {
    public static string Validate(PolicyContext context) __VALIDATION__
    public static string Member(PolicyContext context) __MEMBER__
    public static bool Breaker(PolicyContext context) { return __BREAKER__; }
    __EXPRESSIONS__
}
'@
$source = $source.Replace('__VALIDATION__', $validation.Substring(1)).Replace('__MEMBER__', $member.Substring(1))
$source = $source.Replace('__BREAKER__', $breaker.Substring(1))
$expressionNodes = $policy.SelectNodes('//@*[starts-with(., "@")] | //message[starts-with(., "@")] | //set-body[starts-with(., "@")] | //value[starts-with(., "@")]')
$methods = for ($i = 0; $i -lt $expressionNodes.Count; $i++) {
    $node = $expressionNodes[$i]
    $expression = if ($node -is [System.Xml.XmlAttribute]) { $node.Value } else { $node.InnerText }
    $expression = $expression.Replace('__SESSION_AFFINITY_ENABLED__', 'true')
    $body = if ($expression.StartsWith('@{')) { $expression.Substring(1) } else { '{ return ' + $expression.Substring(1) + '; }' }
    "public static object Expression$i(PolicyContext context) $body"
}
$source = $source.Replace('__EXPRESSIONS__', ($methods -join "`n"))
# PowerShell's bundled Newtonsoft targets an older compatible System.Runtime.
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7', '/nowarn:1701' -ReferencedAssemblies @(
    [Newtonsoft.Json.Linq.JObject].Assembly.Location,
    'System.Runtime',
    'System.Collections',
    'System.Text.RegularExpressions'
)

function New-Request([string]$Scenario = 'stateful') {
    $context = [PolicyContext]::new()
    $context.Request.Headers['X-Lab-Session-Id'] = @('157342bd-322e-46a4-9c05-903db224d8da')
    $context.Request.Headers['X-Lab-Scenario'] = @($Scenario)
    $context.Request.Headers['Content-Type'] = @('application/json; charset=utf-8')
    $context.Request.Body.Value = '{"model":"responses-chat","input":"Synthetic prompt","store":true,"stream":false}'
    if ($Scenario -eq 'stateless') {
        $context.Request.Body.Value = $context.Request.Body.Value.Replace('"store":true', '"store":false')
    }
    $context.Variables['labModel'] = 'responses-chat'
    $context.Variables['labAffinityEnabled'] = $true
    $context.Variables['labHostA'] = 'a.example.invalid'
    $context.Variables['labHostB'] = 'b.example.invalid'
    return $context
}

$script:cases = 0
function Assert-Validation($Context, [string]$Expected) {
    $actual = [PolicyExpressions]::Validate($Context)
    Assert-Contract ($actual -ceq $Expected) "Expected '$Expected', received '$actual'"
    $script:cases++
}

$first = New-Request
$originalBody = $first.Request.Body.Value
Assert-Validation $first ''
Assert-Contract ($first.Request.Body.Preserved -and $first.Request.Body.Value -ceq $originalBody) 'Body must remain intact for forwarding'
Assert-Validation (New-Request 'stateless') ''

$chain = New-Request
$chain.Request.Body.Value = $chain.Request.Body.Value.Replace('"input"', '"previous_response_id":"resp_synthetic","input"')
Assert-Validation $chain 'AffinityCookieRequired'
$chain.Request.Headers['Cookie'] = @('unrelated=value; __AFFINITY_COOKIE__=opaque-value')
Assert-Validation $chain ''
$chain.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=')
Assert-Validation $chain 'AffinityCookieRequired'
$chain.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=first; __AFFINITY_COOKIE__=second')
Assert-Validation $chain 'DuplicateAffinityCookie'
$chain.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=first', '__AFFINITY_COOKIE__=second')
Assert-Validation $chain 'DuplicateAffinityCookie'

$unpinned = New-Request
$unpinned.Variables['labAffinityEnabled'] = $false
$unpinned.Request.Body.Value = $unpinned.Request.Body.Value.Replace('"input"', '"previous_response_id":"resp_synthetic","input"')
Assert-Validation $unpinned ''
$unpinned.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=stale-pin')
Assert-Validation $unpinned 'AffinityCookieDisabled'
$unpinned.Request.Headers.Remove('Cookie') | Out-Null
$unpinned.Request.Body.Value = $unpinned.Request.Body.Value.Replace('"previous_response_id":"resp_synthetic"', '"previous_response_id":null')
Assert-Validation $unpinned 'InvalidPreviousResponseId'

$unexpected = New-Request
$unexpected.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=opaque-value')
Assert-Validation $unexpected 'UnexpectedAffinityCookie'
$stateless = New-Request 'stateless'
$stateless.Request.Headers['Cookie'] = @('__AFFINITY_COOKIE__=opaque-value')
Assert-Validation $stateless 'StatelessRoutingContextNotAllowed'
$stateless.Request.Headers.Remove('Cookie') | Out-Null
$stateless.Request.Body.Value = $stateless.Request.Body.Value.Replace('"input"', '"previous_response_id":"resp_synthetic","input"')
Assert-Validation $stateless 'StatelessRoutingContextNotAllowed'

foreach ($id in @('', 'not-a-uuid', "157342bd-322e-46a4-9c05-903db224d8da`n")) {
    $context = New-Request
    $context.Request.Headers['X-Lab-Session-Id'] = @($id)
    Assert-Validation $context 'InvalidSessionId'
}
$context = New-Request
$context.Request.Headers['X-Lab-Session-Id'] += '157342bd-322e-46a4-9c05-903db224d8da'
Assert-Validation $context 'InvalidSessionId'
$context = New-Request
$context.Request.Headers['X-Lab-Scenario'] = @('anything')
Assert-Validation $context 'InvalidScenario'
$context = New-Request
$context.Request.Url.Query['subscription-key'] = @('synthetic-not-a-key')
Assert-Validation $context 'QueryNotSupported'
$context = New-Request
$context.Request.Headers['Content-Type'] = @('text/plain')
Assert-Validation $context 'JsonContentTypeRequired'
foreach ($body in @('', ' ')) {
    $context = New-Request
    $context.Request.Body.Value = $body
    Assert-Validation $context 'InvalidJson'
}
$context = New-Request
$context.Request.Body = $null
Assert-Validation $context 'InvalidJson'
foreach ($body in @('{', '[]', 'null')) {
    $context = New-Request
    $context.Request.Body.Value = $body
    $failed = $false
    try { [PolicyExpressions]::Validate($context) | Out-Null }
    catch [System.Management.Automation.MethodInvocationException] {
        Assert-Contract ($_.Exception.InnerException -is [Newtonsoft.Json.JsonReaderException]) 'Unexpected parser failure'
        $failed = $true
    }
    Assert-Contract $failed 'Malformed/non-object JSON must raise an error, never forward successfully'
    $script:cases++
}

$invalidBodies = @(
    @('"model":"responses-chat"', '"model":"other"', 'InvalidModel'),
    @('"model":"responses-chat"', '"model":123', 'InvalidModel'),
    @('"stream":false', '"stream":true', 'StreamingNotSupported'),
    @('"stream":false', '"stream":"false"', 'StreamingNotSupported'),
    @('"stream":false', '"unused":false', 'StreamingNotSupported'),
    @('"store":true', '"store":false', 'InvalidStore'),
    @('"store":true', '"store":"true"', 'InvalidStore'),
    @('"store":true', '"unused":true', 'InvalidStore'),
    @('"input"', '"background":true,"input"', 'UnsupportedConversationMode'),
    @('"input"', '"conversation":"conv_synthetic","input"', 'UnsupportedConversationMode'),
    @('"input"', '"previous_response_id":"","input"', 'InvalidPreviousResponseId'),
    @('"input"', '"previous_response_id":null,"input"', 'InvalidPreviousResponseId'),
    @('"input"', '"previous_response_id":42,"input"', 'InvalidPreviousResponseId')
)
foreach ($case in $invalidBodies) {
    $context = New-Request
    $context.Request.Body.Value = $context.Request.Body.Value.Replace($case[0], $case[1])
    Assert-Validation $context $case[2]
}

foreach ($case in @(
    @('a.example.invalid', 'backend-a'),
    @('B.EXAMPLE.INVALID', 'backend-b'),
    @('unknown.example.invalid', '')
)) {
    $context = New-Request
    $context.Request.Url.Host = $case[0]
    Assert-Contract ([PolicyExpressions]::Member($context) -ceq $case[1]) 'Host allow-list classification failed'
    $script:cases++
}

Assert-Contract ($policy.policies.backend.ChildNodes.Count -eq 1) 'Backend must only forward once, with no retry/fallback'
$forward = $policy.SelectSingleNode('/policies/backend/forward-request')
Assert-Contract ($forward.timeout -eq '60' -and $forward.'follow-redirects' -eq 'false' -and $forward.'fail-on-error-status-code' -eq 'false') 'Preserve backend errors without redirects or automatic retries'
$auth = $policy.SelectSingleNode('/policies/inbound/authentication-managed-identity')
Assert-Contract ($auth.resource -eq 'https://cognitiveservices.azure.com' -and $auth.'ignore-error' -eq 'false') 'Require gateway managed identity'
Assert-Contract ($auth.'client-id' -eq '__MANAGED_IDENTITY_CLIENT_ID__') 'Select the dedicated user-assigned identity explicitly'
foreach ($header in @('Ocp-Apim-Subscription-Key', 'api-key', 'Authorization')) {
    Assert-Contract ($null -ne $policy.SelectSingleNode("/policies/inbound/set-header[@name='$header' and @exists-action='delete']")) "Remove caller $header"
}
foreach ($section in @('outbound', 'on-error')) {
    Assert-Contract ($null -ne $policy.SelectSingleNode("/policies/$section/set-header[@name='X-Lab-Request-Id' and @exists-action='override']")) "Correlate $section"
}
Assert-Contract ($null -ne $policy.SelectSingleNode("/policies/on-error/set-header[@name='X-Lab-Backend-Id' and @exists-action='delete']")) 'Never invent an executed backend on a gateway failure'
foreach ($section in @('inbound', 'outbound', 'on-error')) {
    Assert-Contract ($null -ne $policy.SelectSingleNode("/policies/$section/set-header[@name='X-Lab-Error-Reason' and @exists-action='delete']")) "Discard caller/backend error-reason spoofing in $section"
}
foreach ($case in @(@('CircuitBreakerIsOpen', $true), @('BackendConnectionFailure', $false), @('OtherReason', $false))) {
    $context = New-Request
    $context.LastError.Reason = $case[0]
    Assert-Contract ([PolicyExpressions]::Breaker($context) -eq $case[1]) 'Only actual gateway CircuitBreakerIsOpen may set the error-reason header'
    $script:cases++
}
Assert-Contract ($policy.SelectSingleNode('/policies/on-error/choose/when/set-header[@name="X-Lab-Error-Reason"]/value').InnerText -ceq 'CircuitBreakerIsOpen') 'Expose only the fixed safe breaker reason, never raw error messages'
Assert-Contract ($policy.SelectSingleNode('/policies/outbound/choose/when/return-response/set-status').code -eq '502') 'Unknown host must fail explicitly'
foreach ($trace in $policy.SelectNodes('//trace')) {
    Assert-Contract ($trace.source -eq 'responses-lab') 'Use the lab trace source'
    Assert-Contract ($trace.message -notmatch 'Body|Cookie|Authorization|api-key|Subscription-Key|LastError.Message|Request.Url') 'Trace must exclude sensitive/raw data'
    Assert-Contract ($trace.message -match 'context.RequestId') 'Every trace must correlate with gateway logs'
}
Write-Output "Compiled $($expressionNodes.Count) policy expressions as C# 7; passed $script:cases expression cases plus XML, forwarding, authentication, correlation and trace contracts."
