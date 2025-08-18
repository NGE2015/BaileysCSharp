using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace WhatsAppApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly string _logsPath;
        private readonly ILogger<LogsController> _logger;

        public LogsController(ILogger<LogsController> logger, IWebHostEnvironment env)
        {
            _logger = logger;
            _logsPath = Path.Combine(env.ContentRootPath, "logs");
        }

        [HttpGet("files")]
        public IActionResult GetLogFiles()
        {
            try
            {
                if (!Directory.Exists(_logsPath))
                {
                    return Ok(new { files = new List<object>() });
                }

                var files = Directory.GetFiles(_logsPath, "*.log")
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = f,
                        size = new FileInfo(f).Length,
                        lastModified = new FileInfo(f).LastWriteTime,
                        type = Path.GetFileName(f).StartsWith("startup") ? "startup" : "whatsapp"
                    })
                    .OrderByDescending(f => f.lastModified)
                    .ToList();

                return Ok(new { files });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting log files");
                return StatusCode(500, new { message = "Error retrieving log files" });
            }
        }

        [HttpGet("content")]
        public IActionResult GetLogContent(
            [FromQuery] string fileName, 
            [FromQuery] int page = 1, 
            [FromQuery] int pageSize = 100,
            [FromQuery] string? level = null,
            [FromQuery] string? search = null,
            [FromQuery] bool tail = false)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    return BadRequest(new { message = "fileName is required" });
                }

                var filePath = Path.Combine(_logsPath, fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Log file not found" });
                }

                var lines = System.IO.File.ReadAllLines(filePath).ToList();
                
                // Apply filters
                var filteredLines = ApplyFilters(lines, level, search);
                
                // Handle tail request (get most recent lines)
                if (tail)
                {
                    var recentLines = filteredLines.TakeLast(pageSize).ToList();
                    return Ok(new
                    {
                        lines = recentLines.Select((line, index) => ParseLogLine(line, filteredLines.Count - pageSize + index + 1)),
                        totalLines = filteredLines.Count,
                        page = 1,
                        pageSize,
                        totalPages = 1,
                        isTail = true
                    });
                }

                // Pagination
                var totalLines = filteredLines.Count;
                var totalPages = (int)Math.Ceiling((double)totalLines / pageSize);
                var skip = (page - 1) * pageSize;
                var pagedLines = filteredLines.Skip(skip).Take(pageSize).ToList();

                var result = pagedLines.Select((line, index) => ParseLogLine(line, skip + index + 1)).ToList();

                return Ok(new
                {
                    lines = result,
                    totalLines,
                    page,
                    pageSize,
                    totalPages,
                    isTail = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading log file: {fileName}", fileName);
                return StatusCode(500, new { message = "Error reading log file" });
            }
        }

        [HttpGet("tail")]
        public IActionResult TailLog([FromQuery] string fileName, [FromQuery] int lastLineCount = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    return BadRequest(new { message = "fileName is required" });
                }

                var filePath = Path.Combine(_logsPath, fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Log file not found" });
                }

                var lines = System.IO.File.ReadAllLines(filePath).ToList();
                var newLines = lines.Skip(lastLineCount).ToList();

                var result = newLines.Select((line, index) => ParseLogLine(line, lastLineCount + index + 1)).ToList();

                return Ok(new
                {
                    lines = result,
                    totalLineCount = lines.Count,
                    newLinesCount = newLines.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error tailing log file: {fileName}", fileName);
                return StatusCode(500, new { message = "Error tailing log file" });
            }
        }

        private List<string> ApplyFilters(List<string> lines, string? level, string? search)
        {
            var filtered = lines.AsEnumerable();

            // Filter by log level
            if (!string.IsNullOrEmpty(level) && level.ToUpper() != "ALL")
            {
                filtered = filtered.Where(line => line.Contains($" {level.ToUpper()} "));
            }

            // Filter by search term
            if (!string.IsNullOrEmpty(search))
            {
                filtered = filtered.Where(line => 
                    line.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            return filtered.ToList();
        }

        private object ParseLogLine(string line, int lineNumber)
        {
            // Parse Serilog format: [Timestamp Level] Source: Message
            var pattern = @"\[(.+?)\s+(\w+)\]\s*([^:]*?):\s*(.*)";
            var match = Regex.Match(line, pattern);

            if (match.Success)
            {
                return new
                {
                    lineNumber,
                    timestamp = match.Groups[1].Value.Trim(),
                    level = match.Groups[2].Value.Trim(),
                    source = match.Groups[3].Value.Trim(),
                    message = match.Groups[4].Value.Trim(),
                    raw = line
                };
            }

            // Fallback for lines that don't match the pattern
            return new
            {
                lineNumber,
                timestamp = "",
                level = "UNKNOWN",
                source = "",
                message = line,
                raw = line
            };
        }

        [HttpGet("search")]
        public IActionResult SearchLogs(
            [FromQuery] string fileName,
            [FromQuery] string query,
            [FromQuery] string? level = null,
            [FromQuery] bool regex = false,
            [FromQuery] bool caseSensitive = false)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(query))
                {
                    return BadRequest(new { message = "fileName and query are required" });
                }

                var filePath = Path.Combine(_logsPath, fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Log file not found" });
                }

                var lines = System.IO.File.ReadAllLines(filePath).ToList();
                var matchingLines = new List<object>();

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    bool matches = false;

                    // Apply level filter first
                    if (!string.IsNullOrEmpty(level) && level.ToUpper() != "ALL" && !line.Contains($" {level.ToUpper()} "))
                    {
                        continue;
                    }

                    // Apply search
                    if (regex)
                    {
                        try
                        {
                            var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                            matches = Regex.IsMatch(line, query, regexOptions);
                        }
                        catch
                        {
                            return BadRequest(new { message = "Invalid regex pattern" });
                        }
                    }
                    else
                    {
                        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        matches = line.Contains(query, comparison);
                    }

                    if (matches)
                    {
                        matchingLines.Add(ParseLogLine(line, i + 1));
                    }
                }

                return Ok(new
                {
                    matches = matchingLines,
                    totalMatches = matchingLines.Count,
                    searchQuery = query,
                    fileName
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching logs: {fileName}", fileName);
                return StatusCode(500, new { message = "Error searching logs" });
            }
        }
    }
}