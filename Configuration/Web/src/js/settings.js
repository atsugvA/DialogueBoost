/*
 * The plugin's configuration document: read it into the page, and write it back.
 *
 * What is left here after the panels took their own behaviour is the one thing that genuinely
 * spans all three of them — a single form, one save, and three documents behind it. The settings
 * go to Jellyfin's plugin configuration, the selection to its own endpoint and the schedule to
 * Jellyfin's task worker, and each reports its own outcome rather than being assumed to have
 * gone along with the others.
 */

    function parseCsv(str) {
        return str ? str.split(',').map(function (s) { return s.trim(); }).filter(Boolean) : [];
    }

    function joinCsv(arr) {
        return (arr || []).join(', ');
    }

    // The three bitrate modes arrive as a string on a fresh document and as an integer on one
    // written before the enum was named, which is how Jellyfin serialises an enum either way.
    function renderBitrateMode(page, code, profile) {
        var mode = profile.BitrateMode;
        var name = (mode === 'Manual' || mode === 2) ? 'Manual'
                 : (mode === 'Preset' || mode === 1) ? 'Preset'
                 : 'Auto';
        $('#sel' + code + 'BitrateMode', page).val(name);
        $('#txt' + code + 'BitrateKbps', page).val(profile.BitrateKbps || 256);
    }

    function readBitrateMode(page, code, profile) {
        profile.BitrateMode = $('#sel' + code + 'BitrateMode', page).val();
        profile.BitrateKbps = parseInt($('#txt' + code + 'BitrateKbps', page).val(), 10) || 256;
    }

    function httpSuffix(err) {
        return (err && err.status) ? ' (HTTP ' + err.status + ')' : '';
    }

    function loadSchedule(page) {
        return getJson(apiUrl("Plugins/DialogueBoost/Schedule"))
            .then(function (schedule) {
                renderSchedule(page, schedule);
                return schedule;
            })
            .catch(function () {
                $('#scheduleError', page).text(
                    'Could not read the task schedule from the server. What is shown below is not what is stored.');
            });
    }

    function renderSchedule(page, schedule) {
        $('#scheduleError', page).text('');

        renderDailyRun(page, '#chkNormalizationDaily', '#txtNormalizationTime', schedule && schedule.Normalization, '02:00');
        renderDailyRun(page, '#chkCleanupDaily', '#txtCleanupTime', schedule && schedule.Cleanup, '03:00');
    }

    function renderDailyRun(page, checkbox, timeField, run, fallbackTime) {
        $(checkbox, page).prop('checked', !!(run && run.Enabled));
        $(timeField, page).val((run && run.TimeOfDay) || fallbackTime);
    }

    function readDailyRun(page, checkbox, timeField, fallbackTime) {
        return {
            Enabled: $(checkbox, page).is(':checked'),
            TimeOfDay: $(timeField, page).val() || fallbackTime
        };
    }

    function saveSchedule(page) {
        return ApiClient.ajax({
            type: "PUT",
            url: apiUrl("Plugins/DialogueBoost/Schedule"),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({
                Normalization: readDailyRun(page, '#chkNormalizationDaily', '#txtNormalizationTime', '02:00'),
                Cleanup: readDailyRun(page, '#chkCleanupDaily', '#txtCleanupTime', '03:00')
            })
        }).then(function (stored) {
            // Rendered from what came back, so a task Jellyfin has not registered shows
            // as unscheduled instead of as saved.
            renderSchedule(page, stored);
            return stored;
        });
    }

    $('.dialogueBoostConfigurationPage').off('pageshow').on('pageshow', function () {
        Dashboard.showLoadingMsg();
        var page = this;

        bindBand(page);
        bindTabs(page);
        bindTree(page);
        bindSearch(page);
        bindCoverage(page);
        bindProfiles(page);
        bindAdvanced(page);
        loadLanguageHints(page);

        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            // Global
            $('#chkEnabled', page).prop('checked', config.Enabled !== false);
            $('#chkDryRun', page).prop('checked', config.DryRun === true);
            $('#chkPausePlayback', page).prop('checked', config.PauseDuringActivePlayback === true);
            $('#selNewMediaTrigger', page).val(config.NewMediaTrigger === 'WhenSettled' || config.NewMediaTrigger === 1 ? 'WhenSettled' : 'OnScheduledRun');
            $('#txtNewMediaSettleMinutes', page).val(config.NewMediaSettleMinutes || 15);
            newLibraryPolicy = config.NewLibraryPolicy === 'AutoInclude' || config.NewLibraryPolicy === 1 ? 'AutoInclude' : 'FlagOnly';
            $('#selNewLibraryPolicy', page).val(newLibraryPolicy);
            showSettleWindow(page);
            $('#txtMaxConcurrentJobs', page).val(config.MaxConcurrentJobs || 1);
            $('#selSidecarLocation', page).val(sidecarLocationOf(config));
            showSidecarLocation(page);

            // Libraries
            loadSelection(page, true);

            // Track Selection Rules
            var rules = config.TrackSelectionRules || {};
            $('#txtRulesLanguages', page).val(joinCsv(rules.Languages));
            $('#txtRulesCodecs', page).val(joinCsv(rules.AllowedCodecs));
            $('#txtRulesMinChannels', page).val(rules.MinChannels || 0);
            $('#txtRulesMaxChannels', page).val(rules.MaxChannels || 0);
            $('#txtRulesTitleRegex', page).val(rules.TitleRegexPattern || '');
            $('#chkRulesExcludeExternal', page).prop('checked', rules.ExcludeExternalTracks !== false);

            // Dialogue Boost Profile
            var dbProf = config.DialogueBoostProfile || {};
            $('#chkDbEnabled', page).prop('checked', dbProf.Enabled !== false);
            $('#txtCenterGainDb', page).val(dbProf.CenterChannelGainDb || 4.0);
            $('#txtDbLanguages', page).val(joinCsv(dbProf.ProcessLanguages));
            $('#txtDbMarker', page).val(dbProf.SidecarNamingMarker || 'Dialogue Boost');
            $('#chkDbSetDefault', page).prop('checked', dbProf.SetAsDefaultTrack === true);
            renderBitrateMode(page, 'Db', dbProf);

            // Night Mode Profile
            var nmProf = config.NightModeProfile || {};
            $('#chkNmEnabled', page).prop('checked', nmProf.Enabled === true);
            $('#txtNmGain', page).val(nmProf.DynaudnormGain || 15.0);
            $('#txtNmLanguages', page).val(joinCsv(nmProf.ProcessLanguages));
            $('#txtNmMarker', page).val(nmProf.SidecarNamingMarker || 'Broadband Night Mode');
            $('#chkNmSetDefault', page).prop('checked', nmProf.SetAsDefaultTrack === true);
            renderBitrateMode(page, 'Nm', nmProf);

            // Speech Profile
            var spProf = config.SpeechProfile || {};
            $('#chkSpEnabled', page).prop('checked', spProf.Enabled === true);
            $('#txtSpPeak', page).val(spProf.Peak || 0.95);
            $('#txtSpLanguages', page).val(joinCsv(spProf.ProcessLanguages));
            $('#txtSpMarker', page).val(spProf.SidecarNamingMarker || 'Speech Boost');
            $('#chkSpSetDefault', page).prop('checked', spProf.SetAsDefaultTrack === true);
            renderBitrateMode(page, 'Sp', spProf);

            // EBU R128 Profile
            var ebuProf = config.Ebur128Profile || {};
            $('#chkEbuEnabled', page).prop('checked', ebuProf.Enabled === true);
            $('#txtEbuI', page).val(ebuProf.I || -23.0);
            $('#txtEbuTP', page).val(ebuProf.TP || -2.0);
            $('#txtEbuLRA', page).val(ebuProf.LRA || 7.0);
            $('#txtEbuLanguages', page).val(joinCsv(ebuProf.ProcessLanguages));
            $('#txtEbuMarker', page).val(ebuProf.SidecarNamingMarker || 'EBU R128');
            $('#chkEbuSetDefault', page).prop('checked', ebuProf.SetAsDefaultTrack === true);
            renderBitrateMode(page, 'Ebu', ebuProf);

            renderProfileChoices(page, config);

            // Custom Profile
            var custProf = config.CustomProfile || {};
            $('#chkCustEnabled', page).prop('checked', custProf.Enabled === true);
            $('#txtCustFilter', page).val(custProf.FilterString || '');
            $('#txtCustCodec', page).val(custProf.Codec || 'aac');
            $('#txtCustBitrate', page).val(custProf.Bitrate || '256k');
            $('#txtCustLanguages', page).val(joinCsv(custProf.ProcessLanguages));
            $('#txtCustMarker', page).val(custProf.SidecarNamingMarker || 'Custom Track');
            $('#chkCustSetDefault', page).prop('checked', custProf.SetAsDefaultTrack === true);
            renderBitrateMode(page, 'Cust', custProf);

            // The figure and the filenames follow the fields, and the fields have only just
            // arrived; a profile that is on opens rather than hiding behind a chevron.
            renderMeter(page);
            resolveDefaultClaim(page);
            renderOutputNames(page);
            renderBitrateFields(page);
            openEnabledProfiles(page);

            // Normalization Schedule & Actions — the schedule itself comes from
            // Jellyfin, not from the configuration document.
            loadSchedule(page);

            // Watched Cleanup — and whose history decides that.
            $('#chkSkipWatchedItems', page).prop('checked', config.SkipWatchedItems !== false);
            $('#chkIncludeExemptInCleanup', page).prop('checked', config.IncludeExemptInCleanup === true);
            $('#selWatchedBy', page).val(
                config.WatchedBy === 'ChosenAccounts' || config.WatchedBy === 1 ? 'ChosenAccounts' : 'EveryActiveAccount');
            loadWatchedAccounts(page, config);

            Dashboard.hideLoadingMsg();
        });
    });

    $('.dialogueBoostConfigurationPage').on('pagehide', function () {
        unbindBand(this);
    });

    $('.dialogueBoostConfigurationPage').off('submit').on('submit', function () {
        Dashboard.showLoadingMsg();
        var page = this;

        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            // Global
            config.Enabled = $('#chkEnabled', page).is(':checked');
            config.DryRun = $('#chkDryRun', page).is(':checked');
            config.PauseDuringActivePlayback = $('#chkPausePlayback', page).is(':checked');
            config.NewLibraryPolicy = $('#selNewLibraryPolicy', page).val();
            config.NewMediaTrigger = $('#selNewMediaTrigger', page).val();
            config.NewMediaSettleMinutes = parseInt($('#txtNewMediaSettleMinutes', page).val(), 10) || 15;
            config.SkipWatchedItems = $('#chkSkipWatchedItems', page).is(':checked');
            config.IncludeExemptInCleanup = $('#chkIncludeExemptInCleanup', page).is(':checked');
            config.WatchedBy = $('#selWatchedBy', page).val();
            config.WatchedByUserIds = readWatchedAccounts(page);
            config.MaxConcurrentJobs = parseInt($('#txtMaxConcurrentJobs', page).val(), 10) || 1;
            config.SidecarLocation = $('#selSidecarLocation', page).val();

            // Track Selection Rules
            config.TrackSelectionRules = config.TrackSelectionRules || {};
            config.TrackSelectionRules.Languages = parseCsv($('#txtRulesLanguages', page).val());
            config.TrackSelectionRules.AllowedCodecs = parseCsv($('#txtRulesCodecs', page).val());
            config.TrackSelectionRules.MinChannels = parseInt($('#txtRulesMinChannels', page).val(), 10) || 0;
            config.TrackSelectionRules.MaxChannels = parseInt($('#txtRulesMaxChannels', page).val(), 10) || 0;
            config.TrackSelectionRules.TitleRegexPattern = $('#txtRulesTitleRegex', page).val().trim();
            config.TrackSelectionRules.ExcludeExternalTracks = $('#chkRulesExcludeExternal', page).is(':checked');

            // Dialogue Boost Profile
            config.DialogueBoostProfile = config.DialogueBoostProfile || {};
            config.DialogueBoostProfile.Enabled = $('#chkDbEnabled', page).is(':checked');
            config.DialogueBoostProfile.CenterChannelGainDb = parseFloat($('#txtCenterGainDb', page).val()) || 4.0;
            config.DialogueBoostProfile.ProcessLanguages = parseCsv($('#txtDbLanguages', page).val());
            config.DialogueBoostProfile.SidecarNamingMarker = $('#txtDbMarker', page).val().trim() || 'Dialogue Boost';
            config.DialogueBoostProfile.SetAsDefaultTrack = $('#chkDbSetDefault', page).is(':checked');
            readBitrateMode(page, 'Db', config.DialogueBoostProfile);

            // Night Mode Profile
            config.NightModeProfile = config.NightModeProfile || {};
            config.NightModeProfile.Enabled = $('#chkNmEnabled', page).is(':checked');
            config.NightModeProfile.DynaudnormGain = parseFloat($('#txtNmGain', page).val()) || 15.0;
            config.NightModeProfile.ProcessLanguages = parseCsv($('#txtNmLanguages', page).val());
            config.NightModeProfile.SidecarNamingMarker = $('#txtNmMarker', page).val().trim() || 'Broadband Night Mode';
            config.NightModeProfile.SetAsDefaultTrack = $('#chkNmSetDefault', page).is(':checked');
            readBitrateMode(page, 'Nm', config.NightModeProfile);

            // Speech Profile
            config.SpeechProfile = config.SpeechProfile || {};
            config.SpeechProfile.Enabled = $('#chkSpEnabled', page).is(':checked');
            config.SpeechProfile.Peak = parseFloat($('#txtSpPeak', page).val()) || 0.95;
            config.SpeechProfile.ProcessLanguages = parseCsv($('#txtSpLanguages', page).val());
            config.SpeechProfile.SidecarNamingMarker = $('#txtSpMarker', page).val().trim() || 'Speech Boost';
            config.SpeechProfile.SetAsDefaultTrack = $('#chkSpSetDefault', page).is(':checked');
            readBitrateMode(page, 'Sp', config.SpeechProfile);

            // EBU R128 Profile
            config.Ebur128Profile = config.Ebur128Profile || {};
            config.Ebur128Profile.Enabled = $('#chkEbuEnabled', page).is(':checked');
            config.Ebur128Profile.I = parseFloat($('#txtEbuI', page).val()) || -23.0;
            config.Ebur128Profile.TP = parseFloat($('#txtEbuTP', page).val()) || -2.0;
            config.Ebur128Profile.LRA = parseFloat($('#txtEbuLRA', page).val()) || 7.0;
            config.Ebur128Profile.ProcessLanguages = parseCsv($('#txtEbuLanguages', page).val());
            config.Ebur128Profile.SidecarNamingMarker = $('#txtEbuMarker', page).val().trim() || 'EBU R128';
            config.Ebur128Profile.SetAsDefaultTrack = $('#chkEbuSetDefault', page).is(':checked');
            readBitrateMode(page, 'Ebu', config.Ebur128Profile);

            // Custom Profile
            config.CustomProfile = config.CustomProfile || {};
            config.CustomProfile.Enabled = $('#chkCustEnabled', page).is(':checked');
            config.CustomProfile.FilterString = $('#txtCustFilter', page).val().trim();
            config.CustomProfile.Codec = $('#txtCustCodec', page).val().trim() || 'aac';
            config.CustomProfile.Bitrate = $('#txtCustBitrate', page).val().trim() || '256k';
            config.CustomProfile.ProcessLanguages = parseCsv($('#txtCustLanguages', page).val());
            config.CustomProfile.SidecarNamingMarker = $('#txtCustMarker', page).val().trim() || 'Custom Track';
            config.CustomProfile.SetAsDefaultTrack = $('#chkCustSetDefault', page).is(':checked');
            readBitrateMode(page, 'Cust', config.CustomProfile);

            // The selection and the schedule are documents of their own, behind their
            // own endpoints — so each is saved separately and reports its own outcome
            // rather than being assumed to have gone along with the settings.
            ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function (result) {
                var failed = [];

                saveSelection(page).catch(function (err) {
                    failed.push('the library selection' + httpSuffix(err));
                    return loadSelection(page, false);
                }).then(function () {
                    return saveSchedule(page).catch(function (err) {
                        failed.push('the task schedule' + httpSuffix(err));
                        return loadSchedule(page);
                    });
                }).then(function () {
                    // Saving changes what the band is reporting — a profile switched on adds a
                    // track to every covered item — so the numbers are re-read, not left stale.
                    loadBandCounts();

                    if (failed.length === 0) {
                        Dashboard.processPluginConfigurationUpdateResult(result);
                        return;
                    }

                    Dashboard.hideLoadingMsg();
                    Dashboard.alert({
                        title: 'Not everything was saved',
                        message: 'The settings were saved, but ' + failed.join(' and ') +
                                 ' did not. Nothing there was changed on the server; the page now shows what is actually stored.'
                    });
                });
            });
        });

        return false;
    });
