@echo off
setlocal

echo.
echo Testing ASHA's saved Groq key with Qwen 3.6 27B...
echo This sends one tiny test request. Your API key will not be displayed.
echo.

powershell -NoProfile -Command "$key = [Environment]::GetEnvironmentVariable('ASHA_GROQ_API_KEY', 'User'); $model = [Environment]::GetEnvironmentVariable('ASHA_GROQ_MODEL', 'User'); if ([string]::IsNullOrWhiteSpace($key)) { Write-Host 'No ASHA Groq key is saved. Run configure-groq.bat first.'; exit 1 }; if ($model -ne 'qwen/qwen3.6-27b') { Write-Host ('ASHA is currently configured for: ' + $model); Write-Host 'Run use-qwen3.6-27b.bat first, then test again.'; exit 1 }; $headers = @{ Authorization = 'Bearer ' + $key }; $body = @{ model = $model; messages = @(@{ role = 'user'; content = 'Reply exactly: QWEN READY' }); reasoning_effort = 'none'; max_tokens = 8; temperature = 0 } | ConvertTo-Json -Depth 5; try { $response = Invoke-RestMethod -Method Post -Uri 'https://api.groq.com/openai/v1/chat/completions' -Headers $headers -ContentType 'application/json' -Body $body; Write-Host 'SUCCESS: ASHA is configured for Qwen and Groq accepted the request.'; Write-Host ('Model returned: ' + $response.model); Write-Host ('Reply: ' + $response.choices[0].message.content) } catch { Write-Host 'FAILED: Groq did not accept this Qwen request.'; Write-Host $_.Exception.Message; exit 1 }"

echo.
pause
