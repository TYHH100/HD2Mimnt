[CmdletBinding()]
param(
    [string]$GameDataPath = "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data"
)

$ErrorActionPreference = "Stop"
$ReaderRoot = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $PSScriptRoot "unit_inspections"

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.Encoding]::UTF8)
}

function Convert-UnitReportToCompact {
    param(
        [object]$Report,
        [object]$Job,
        [int]$ExitCode
    )

    $geometry = @()
    foreach ($mesh in @($Report.geometry)) {
        $attributes = @()
        foreach ($attribute in @($mesh.attributes)) {
            $attributes += [ordered]@{
                component_type = $attribute.component_type
                component_name = $attribute.component_name
                component_index = $attribute.component_index
                format = $attribute.format
                format_name = $attribute.format_name
                decoded_semantics = $attribute.decoded_semantics
            }
        }

        $sections = @()
        foreach ($section in @($mesh.sections)) {
            $sections += [ordered]@{
                index = $section.index
                material_index = $section.material_index
                material_slot_id = $section.material_slot_id
                material_resource_id = $section.material_resource_id
                vertex_offset = $section.vertex_offset
                num_vertices = $section.num_vertices
                index_offset = $section.index_offset
                num_indices = $section.num_indices
                group_index = $section.group_index
                invalid_index_count = $section.invalid_index_count
            }
        }

        $skinning = $mesh.skinning
        $compactSkinning = $null
        if ($null -ne $skinning) {
            $palette = @()
            foreach ($bone in @($skinning.palette)) {
                $palette += [ordered]@{
                    palette_slot = $bone.palette_slot
                    transform_index = $bone.transform_index
                    name_hash = $bone.name_hash
                    name = $bone.name
                    valid = $bone.valid
                }
            }

            $sectionSkinning = @()
            foreach ($section in @($skinning.section_skinning)) {
                $sets = @()
                foreach ($set in @($section.bone_index_sets)) {
                    $sets += [ordered]@{
                        component_index = $set.component_index
                        resolved_active_influence_count = $set.resolved_active_influence_count
                        unresolved_active_influence_count = $set.unresolved_active_influence_count
                        out_of_range_vertex_count = $set.out_of_range_vertex_count
                    }
                }
                $sectionSkinning += [ordered]@{
                    section_index = $section.section_index
                    material_index = $section.material_index
                    material_slot_id = $section.material_slot_id
                    status = $section.status
                    resolved_active_influence_count = $section.resolved_active_influence_count
                    unresolved_active_influence_count = $section.unresolved_active_influence_count
                    out_of_range_vertex_count = $section.out_of_range_vertex_count
                    bone_index_sets = $sets
                }
            }

            $compactSkinning = [ordered]@{
                status = $skinning.status
                lod_index = $skinning.lod_index
                bone_info_index = $skinning.bone_info_index
                weight_attribute = $skinning.weight_attribute
                bone_index_set_count = $skinning.bone_index_set_count
                resolved_active_influence_count = $skinning.resolved_active_influence_count
                unresolved_active_influence_count = $skinning.unresolved_active_influence_count
                out_of_range_vertex_count = $skinning.out_of_range_vertex_count
                palette = $palette
                section_skinning = $sectionSkinning
                matrix_validation = $skinning.matrix_validation
            }
        }

        $geometry += [ordered]@{
            mesh_id = if ($mesh.mesh_id -is [string]) { $mesh.mesh_id } else { "{0:x8}" -f [uint32]$mesh.mesh_id }
            mesh_info_index = $mesh.mesh_info_index
            transform_index = $mesh.transform_index
            lod_index = $mesh.lod_index
            stream_index = $mesh.stream_index
            vertex_count = $mesh.vertex_count
            vertex_offset = $mesh.vertex_offset
            vertex_stride = $mesh.vertex_stride
            index_count = $mesh.index_count
            triangle_count = $mesh.triangle_count
            attributes = $attributes
            sections = $sections
            skinning = $compactSkinning
        }
    }

    $streams = @()
    foreach ($stream in @($Report.stream_info.records)) {
        $components = @()
        foreach ($component in @($stream.components)) {
            $components += [ordered]@{
                type = $component.type
                type_name = $component.type_name
                format = $component.format
                format_name = $component.format_name
                index = $component.index
                byte_size = $component.byte_size
                byte_offset = $component.byte_offset
            }
        }
        $streams += [ordered]@{
            index = $stream.index
            component_info_id = $stream.component_info_id
            components = $components
            num_vertices = $stream.num_vertices
            vertex_stride = $stream.vertex_stride
            vertex_buffer_id = $stream.vertex_buffer_id
            vertex_buffer_offset = $stream.vertex_buffer_offset
            vertex_buffer_size = $stream.vertex_buffer_size
            index_buffer_id = $stream.index_buffer_id
            num_indices = $stream.num_indices
            index_buffer_type = $stream.index_buffer_type
            index_buffer_offset = $stream.index_buffer_offset
            index_buffer_size = $stream.index_buffer_size
            warnings = @($stream.warnings)
        }
    }

    $boneRecords = @()
    foreach ($record in @($Report.bone_info.records)) {
        $resolvedPalette = @()
        foreach ($bone in @($record.resolved_real_indices)) {
            $resolvedPalette += [ordered]@{
                palette_slot = $bone.palette_slot
                transform_index = $bone.transform_index
                name_hash = $bone.name_hash
                name = $bone.name
                valid = $bone.valid
            }
        }
        $boneRecords += [ordered]@{
            index = $record.index
            num_bones = $record.num_bones
            real_indices = @($record.real_indices)
            remaps = @($record.remaps)
            resolved_real_indices = $resolvedPalette
            resolved_remaps = @($record.resolved_remaps)
            warnings = @($record.warnings)
        }
    }

    $boneNames = @()
    foreach ($bone in @($Report.bones_resource.bones)) {
        $boneNames += [ordered]@{
            hash = $bone.hash
            name = $bone.name
        }
    }

    return [ordered]@{
        schema = "SourceModelReader-GlassUnitCompact1"
        source_job = $Job.Name
        source_material_file_id = $Job.Material
        source_package = $Job.Package
        inspection_exit_code = $ExitCode
        package = $Report.package
        unit = $Report.unit
        summary = $Report.summary
        materials = @($Report.materials)
        bones_resource = if ($null -ne $Report.bones_resource) {
            [ordered]@{
                file_id = $Report.bones_resource.file_id
                bone_count = $Report.bones_resource.bone_count
                bones = $boneNames
            }
        } else { $null }
        transform_info = [ordered]@{
            count = $Report.transform_info.count
            resolved_skeleton = @($Report.transform_info.resolved_skeleton)
            weight_usage_summary = $Report.transform_info.weight_usage_summary
            warnings = @($Report.transform_info.warnings)
        }
        bone_info = [ordered]@{
            count = $Report.bone_info.count
            records = $boneRecords
            warnings = @($Report.bone_info.warnings)
        }
        stream_info = [ordered]@{
            count = $Report.stream_info.count
            records = $streams
            warnings = @($Report.stream_info.warnings)
        }
        mesh_info = $Report.mesh_info
        geometry = $geometry
        warnings = @($Report.warnings)
        limitations = @($Report.limitations)
    }
}

Push-Location $ReaderRoot
try {
    if (-not (Test-Path -LiteralPath $GameDataPath -PathType Container)) {
        throw "Game data directory not found: $GameDataPath"
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    Write-Host "== SourceModelReader focused regression tests ==" -ForegroundColor Cyan
    $testModules = @("test_hd2_texture.py", "test_hd2_material_users.py")
    foreach ($module in $testModules) {
        Write-Host ""
        Write-Host "Running $module ..."
        $testOutput = & py -3 -m unittest discover -s tests -p $module -v 2>&1
        $testCode = $LASTEXITCODE
        $testLog = Join-Path $OutputDir ($module + ".log")
        $testOutput | Out-File -LiteralPath $testLog -Encoding utf8
        $testOutput | ForEach-Object { Write-Host $_ }
        if ($testCode -ne 0) {
            throw "Regression tests failed for $module (exit code $testCode). See $testLog"
        }
    }

    $jobs = @(
        @{ Name="glass_9423_direct"; Package="09985dc611a3a8b6"; Unit="12738f0c0c22b686"; Material="9423bb5f9a715c2e" },
        @{ Name="glass_2f803_common"; Package="029a193f2a08167c"; Unit="246144231754fff4"; Material="2f80391371cce33e" },
        @{ Name="glass_8b3ff_direct"; Package="01edaa2a7e84b840"; Unit="2d3bc1683a54298d"; Material="8b3ff5502ac52c25" },
        @{ Name="glass_0f320_lod"; Package="1e33cc1600ff38f3"; Unit="4e401cc877093220"; Material="0f3201437c248dfc" },
        @{ Name="glass_9423_composite"; Package="fc5b6bff0db90aab"; Unit="5367951cd4da72d9"; Material="9423bb5f9a715c2e" }
    )

    $batchResults = @()
    $failures = @()

    foreach ($job in $jobs) {
        Write-Host ""
        Write-Host "== Inspecting $($job.Name) ==" -ForegroundColor Cyan
        $rawPath = Join-Path ([System.IO.Path]::GetTempPath()) ("smr-" + [guid]::NewGuid().ToString("N") + ".json")
        $compactPath = Join-Path $OutputDir ($job.Name + ".json")
        $unitArgs = @(
            "-3",
            ".\source_model_reader.py",
            "hd2-unit",
            $GameDataPath,
            "--package", $job.Package,
            "--unit", $job.Unit,
            "--json", $rawPath
        )
        $textOutput = & py @unitArgs 2>&1
        $exitCode = $LASTEXITCODE
        $textOutput | ForEach-Object { Write-Host $_ }

        if ($exitCode -ge 2 -or -not (Test-Path -LiteralPath $rawPath -PathType Leaf)) {
            $failures += [ordered]@{ name=$job.Name; package=$job.Package; unit=$job.Unit; exit_code=$exitCode; message="Unit inspection failed or report was not created." }
            if (Test-Path -LiteralPath $rawPath) { Remove-Item -LiteralPath $rawPath -Force }
            continue
        }

        try {
            $report = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
            $compact = Convert-UnitReportToCompact -Report $report -Job $job -ExitCode $exitCode
            Write-JsonFile -Path $compactPath -Value $compact

            $weightSummary = $report.transform_info.weight_usage_summary
            $batchResults += [ordered]@{
                name = $job.Name
                package = $job.Package
                unit = $job.Unit
                material = $job.Material
                status = $report.summary.status
                exit_code = $exitCode
                geometry_source = $report.unit.geometry_source
                transform_count = $report.summary.transform_count
                bone_info_count = $report.summary.bone_info_count
                stream_count = $report.summary.stream_count
                mesh_count = $report.summary.mesh_count
                decoded_mesh_count = $report.summary.decoded_mesh_count
                vertex_count = $report.summary.vertex_count
                index_count = $report.summary.index_count
                triangle_count = $report.summary.triangle_count
                actively_weighted_transform_count = $weightSummary.actively_weighted_transform_count
                named_active_transform_count = $weightSummary.actively_weighted_named_transform_count
                unresolved_active_influence_lane_count = $weightSummary.unresolved_active_influence_lane_count
                warning_count = @($report.warnings).Count
                compact_report = $compactPath
            }
            Write-Host "Compact report: $compactPath"
        }
        catch {
            $failures += [ordered]@{ name=$job.Name; package=$job.Package; unit=$job.Unit; exit_code=$exitCode; message=$_.Exception.Message }
        }
        finally {
            Remove-Item -LiteralPath $rawPath -Force -ErrorAction SilentlyContinue
        }
    }

    $summaryPath = Join-Path $OutputDir "glass_unit_batch_summary.json"
    $summary = [ordered]@{
        schema = "SourceModelReader-GlassUnitBatchSummary1"
        game_data_path = $GameDataPath
        jobs_requested = $jobs.Count
        jobs_succeeded = $batchResults.Count
        jobs_failed = $failures.Count
        test_modules = $testModules
        results = $batchResults
        failures = $failures
    }
    Write-JsonFile -Path $summaryPath -Value $summary

    $markdownPath = Join-Path $OutputDir "glass_unit_batch_summary.md"
    $lines = @(
        "# Glass Unit Batch Inspection",
        "",
        "Jobs: $($jobs.Count) | Succeeded: $($batchResults.Count) | Failed: $($failures.Count)",
        "",
        "| Candidate | Status | Geometry | Transforms | BoneInfo | Meshes | Vertices | Active weighted transforms | Unresolved lanes | Warnings |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|---:|"
    )
    foreach ($item in $batchResults) {
        $lines += "| $($item.name) | $($item.status) | $($item.geometry_source) | $($item.transform_count) | $($item.bone_info_count) | $($item.decoded_mesh_count) | $($item.vertex_count) | $($item.actively_weighted_transform_count) | $($item.unresolved_active_influence_lane_count) | $($item.warning_count) |"
    }
    if ($failures.Count -gt 0) {
        $lines += ""
        $lines += "## Failures"
        foreach ($failure in $failures) {
            $lines += "- $($failure.name): $($failure.message)"
        }
    }
    [System.IO.File]::WriteAllLines($markdownPath, [string[]]$lines, [System.Text.Encoding]::UTF8)

    Write-Host ""
    Write-Host "== Batch complete ==" -ForegroundColor Green
    Write-Host "Succeeded: $($batchResults.Count) / $($jobs.Count)"
    Write-Host "Summary JSON: $summaryPath"
    Write-Host "Summary Markdown: $markdownPath"

    if ($failures.Count -gt 0) {
        Write-Error "$($failures.Count) Unit inspection(s) failed. Successful reports were preserved."
        exit 2
    }
}
finally {
    Pop-Location
}
